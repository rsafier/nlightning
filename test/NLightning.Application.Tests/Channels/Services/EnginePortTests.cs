using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// The engine port adapters (NL-230) with a mocked transaction pipeline: holder checks, channel lookup, the
/// <see cref="CommitmentSpec"/> → <see cref="CommitmentTxSpec"/> mapping and the failure mapping of the verifier.
/// The real-signature behaviour is in <see cref="EngineCommitmentPortsTwoNodeTests"/>.
/// </summary>
public class EnginePortTests
{
    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
    };

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly Hash s_hash = new(new byte[32]);

    private readonly Mock<ICommitmentTransactionModelFactory> _factory = new();
    private readonly Mock<ICommitmentTransactionBuilder> _commitmentBuilder = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly ChannelModel _channel;
    private readonly CommitmentTransactionModel _model;
    private readonly SignedTransaction _unsignedTx = new(TxId.One, [0x02, 0x00]);
    private readonly CompactSignature _signature = new(new byte[64]);

    public EnginePortTests()
    {
        var commitmentNumber = new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(10_000), s_pubKey, s_pubKey)
        {
            TransactionId = TxId.One,
            Index = 0
        };
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                                     LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        _channel = new ChannelModel(channelParams, s_channelId, commitmentNumber, fundingOutput, true, null, null,
                                    LightningMoney.Satoshis(10_000), keySet, 0, 0, LightningMoney.Zero, keySet, 0,
                                    s_pubKey, 0, ChannelState.Open, ChannelVersion.V1);
        var channel = _channel;
        _channels.Setup(x => x.TryGetChannel(s_channelId, out channel)).Returns(true);
        _model = new CommitmentTransactionModel(commitmentNumber, 7, LightningMoney.Zero, fundingOutput)
        {
            PerCommitmentPoint = s_pubKey
        };
        _commitmentBuilder.Setup(x => x.BuildWithOutputMap(_model))
                          .Returns(new CommitmentTransactionBuildResult(_unsignedTx, []));
    }

    [Fact]
    public void Given_RemoteSpec_When_Signing_Then_TheLocalViewSpecIsSignedForTheRemoteSide()
    {
        // Arrange - we offered HTLC 3 (Outgoing), the peer offered HTLC 5 (Incoming)
        var spec = new CommitmentSpec(CommitmentSide.Remote, 1_234, 6_000_000, 3_000_000,
                                      [
                                          new SpecHtlc(HtlcDirection.Outgoing, 3, 400_000, s_hash, 600),
                                          new SpecHtlc(HtlcDirection.Incoming, 5, 600_000, s_hash, 601)
                                      ]);
        _factory.Setup(x => x.CreateCommitmentTransactionModel(
                           _channel, It.Is<CommitmentTxSpec>(s => s.ToLocalMsat == 6_000_000
                                                              && s.ToRemoteMsat == 3_000_000
                                                              && s.FeeRatePerKw == 1_234 && s.Htlcs.Count == 2),
                           CommitmentSide.Remote, 7, s_pubKey))
                .Returns(_model);
        _signer.Setup(x => x.SignChannelTransaction(s_channelId, _unsignedTx)).Returns(_signature);
        _signer.Setup(x => x.SignRemoteHtlcTransactions(s_channelId, It.IsAny<IReadOnlyList<HtlcSigningContext>>()))
               .Returns([]);
        var port = new EngineCommitmentSignerPort(CreateService(), _channels.Object);

        // Act
        var signatures = port.SignRemoteCommitment(s_channelId, 7, spec, s_pubKey);

        // Assert
        Assert.Equal(_signature, signatures.Signature);
        Assert.Empty(signatures.HtlcSignatures);
    }

    [Fact]
    public void Given_EngineSpecWithHtlcs_When_AdaptedForTheFactory_Then_HtlcsCarryNoAddMessage()
    {
        // Arrange - NL-244: the adapter used to pass null! for a non-nullable AddMessage
        var spec = new CommitmentSpec(CommitmentSide.Remote, 1_234, 6_000_000, 3_000_000,
                                      [new SpecHtlc(HtlcDirection.Incoming, 5, 600_000, s_hash, 601)]);

        // Act
        var htlc = Assert.Single(CommitmentTxSpec.FromCommitmentSpec(spec).Htlcs);

        // Assert
        Assert.Null(htlc.AddMessage);
        Assert.Equal(5UL, htlc.Id);
        Assert.Equal(601U, htlc.CltvExpiry);
        Assert.Equal(HtlcDirection.Incoming, htlc.Direction);
    }

    [Fact]
    public void Given_LocalSpec_When_Signing_Then_Throws()
    {
        // Arrange
        var port = new EngineCommitmentSignerPort(CreateService(), _channels.Object);
        var spec = new CommitmentSpec(CommitmentSide.Local, 253, 1, 1, []);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => port.SignRemoteCommitment(s_channelId, 1, spec, s_pubKey));
    }

    [Fact]
    public void Given_UnknownChannel_When_Signing_Then_Throws()
    {
        // Arrange
        var port = new EngineCommitmentSignerPort(CreateService(), _channels.Object);
        var spec = new CommitmentSpec(CommitmentSide.Remote, 253, 1, 1, []);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => port.SignRemoteCommitment(ChannelId.Zero, 1, spec, s_pubKey));
    }

    [Fact]
    public void Given_ValidSignatures_When_Verifying_Then_True()
    {
        // Arrange
        var spec = new CommitmentSpec(CommitmentSide.Local, 253, 6_000_000, 4_000_000, []);
        _factory.Setup(x => x.CreateCommitmentTransactionModel(_channel, It.IsAny<CommitmentTxSpec>(),
                                                                CommitmentSide.Local, 7, null))
                .Returns(_model);
        var port = CreateVerifierPort();

        // Act
        var valid = port.VerifyLocalCommitment(s_channelId, 7, spec, new CommitmentSignatures(_signature, []));

        // Assert
        Assert.True(valid);
        _signer.Verify(x => x.ValidateSignature(s_channelId, _signature, _unsignedTx), Times.Once);
    }

    [Fact]
    public void Given_SignerRejectsTheSignature_When_Verifying_Then_FalseInsteadOfAnException()
    {
        // Arrange
        var spec = new CommitmentSpec(CommitmentSide.Local, 253, 6_000_000, 4_000_000, []);
        _factory.Setup(x => x.CreateCommitmentTransactionModel(_channel, It.IsAny<CommitmentTxSpec>(),
                                                                CommitmentSide.Local, 7, null))
                .Returns(_model);
        _signer.Setup(x => x.ValidateSignature(s_channelId, _signature, _unsignedTx))
               .Throws(new SignerException("Peer signature is invalid", s_channelId));
        var port = CreateVerifierPort();

        // Act
        var valid = port.VerifyLocalCommitment(s_channelId, 7, spec, new CommitmentSignatures(_signature, []));

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public void Given_RemoteSpec_When_Verifying_Then_Throws()
    {
        // Arrange
        var spec = new CommitmentSpec(CommitmentSide.Remote, 253, 1, 1, []);
        var port = CreateVerifierPort();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => port.VerifyLocalCommitment(s_channelId, 1, spec,
                                                                           new CommitmentSignatures(_signature, [])));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_Secret_When_CheckingRevocation_Then_DelegatesToThePerCommitmentSecretVerifier(bool expected)
    {
        // Arrange
        var secret = new Secret(Enumerable.Repeat((byte)7, 32).ToArray());
        var verifier = new Mock<IPerCommitmentSecretVerifier>();
        verifier.Setup(x => x.Verify(secret, s_pubKey)).Returns(expected);
        var port = new EngineRevocationVerifierPort(verifier.Object);

        // Act
        var valid = port.IsValidSecret(secret, s_pubKey);

        // Assert
        Assert.Equal(expected, valid);
    }

    private EngineCommitmentVerifierPort CreateVerifierPort() =>
        new(CreateService(), _channels.Object, NullLogger<EngineCommitmentVerifierPort>.Instance);

    private CommitmentSigningService CreateService() =>
        new(_factory.Object, _commitmentBuilder.Object, new Mock<IHtlcTransactionBuilder>().Object, _signer.Object);
}