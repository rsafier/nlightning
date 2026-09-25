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
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Wiring of <see cref="CommitmentSigningService"/> (BOLT2 plan N3-T5). The Appendix C/F signature vectors run in
/// <c>NLightning.Integration.Tests.BOLT3.CommitmentSigningServiceVectorTests</c>.
/// </summary>
public class CommitmentSigningServiceTests
{
    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<ICommitmentTransactionModelFactory> _factory = new();
    private readonly Mock<ICommitmentTransactionBuilder> _commitmentBuilder = new();
    private readonly Mock<IHtlcTransactionBuilder> _htlcBuilder = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly ChannelModel _channel;
    private readonly CommitmentSpec _spec = new(5_000_000, 5_000_000, 253, []);
    private readonly CommitmentTransactionModel _model;
    private readonly SignedTransaction _unsignedTx = new(TxId.One, [0x02, 0x00]);
    private readonly CommitmentSignatures _expectedEmpty;
    private readonly CompactSignature _signature = new(new byte[64]);

    public CommitmentSigningServiceTests()
    {
        var commitmentNumber = new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(10_000), s_pubKey, s_pubKey)
        {
            TransactionId = TxId.One,
            Index = 0
        };
        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        _channel = new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutput, true, null, null,
                                    LightningMoney.Satoshis(10_000), keySet, 0, 0, LightningMoney.Zero, keySet, 0,
                                    s_pubKey, 0, ChannelState.Open, ChannelVersion.V1);
        _model = new CommitmentTransactionModel(commitmentNumber, 7, LightningMoney.Zero, fundingOutput)
        {
            PerCommitmentPoint = s_pubKey
        };
        _commitmentBuilder.Setup(x => x.BuildWithOutputMap(_model))
                          .Returns(new CommitmentTransactionBuildResult(_unsignedTx, []));
        _expectedEmpty = new CommitmentSignatures(TxId.One, _signature, []);
    }

    [Fact]
    public void Given_RemoteCommitment_When_Signing_Then_BuildsTheRemoteSideWithThePointAndSignsIt()
    {
        // Arrange
        _factory.Setup(x => x.CreateCommitmentTransactionModel(_channel, _spec, CommitmentSide.Remote, 7, s_pubKey))
                .Returns(_model);
        _signer.Setup(x => x.SignChannelTransaction(ChannelId.Zero, _unsignedTx)).Returns(_signature);
        _signer.Setup(x => x.SignRemoteHtlcTransactions(ChannelId.Zero, It.IsAny<IReadOnlyList<HtlcSigningContext>>()))
               .Returns([]);
        var service = CreateService();

        // Act
        var result = service.SignRemoteCommitment(_channel, _spec, 7, s_pubKey);

        // Assert
        Assert.Equal(_expectedEmpty.CommitmentTxId, result.CommitmentTxId);
        Assert.Equal(_signature, result.Signature);
        Assert.Empty(result.HtlcSignatures);
        _signer.Verify(x => x.SignRemoteHtlcTransactions(
                           ChannelId.Zero, It.Is<IReadOnlyList<HtlcSigningContext>>(l => l.Count == 0)), Times.Once);
    }

    [Fact]
    public void Given_LocalCommitment_When_Verifying_Then_ChecksCommitmentAndHtlcSignatures()
    {
        // Arrange
        _factory.Setup(x => x.CreateCommitmentTransactionModel(_channel, _spec, CommitmentSide.Local, 7, null))
                .Returns(_model);
        var service = CreateService();

        // Act
        var result = service.VerifyLocalCommitment(_channel, _spec, 7, _signature, []);

        // Assert
        Assert.Equal(TxId.One, result.CommitmentTxId);
        _signer.Verify(x => x.ValidateSignature(ChannelId.Zero, _signature, _unsignedTx), Times.Once);
        _signer.Verify(x => x.ValidateLocalHtlcSignatures(ChannelId.Zero,
                                                          It.Is<IReadOnlyList<HtlcSigningContext>>(l => l.Count == 0),
                                                          It.Is<IReadOnlyList<CompactSignature>>(l => l.Count == 0)),
                       Times.Once);
    }

    [Fact]
    public void Given_InvalidCommitmentSignature_When_Verifying_Then_SignerExceptionPropagates()
    {
        // Arrange
        _factory.Setup(x => x.CreateCommitmentTransactionModel(_channel, _spec, CommitmentSide.Local, 7, null))
                .Returns(_model);
        _signer.Setup(x => x.ValidateSignature(ChannelId.Zero, _signature, _unsignedTx))
               .Throws(new SignerException("Peer signature is invalid", ChannelId.Zero));
        var service = CreateService();

        // Act / Assert
        Assert.Throws<SignerException>(() => service.VerifyLocalCommitment(_channel, _spec, 7, _signature, []));
        _signer.Verify(x => x.ValidateLocalHtlcSignatures(It.IsAny<ChannelId>(),
                                                          It.IsAny<IReadOnlyList<HtlcSigningContext>>(),
                                                          It.IsAny<IReadOnlyList<CompactSignature>>()),
                       Times.Never);
    }

    private CommitmentSigningService CreateService() =>
        new(_factory.Object, _commitmentBuilder.Object, _htlcBuilder.Object, _signer.Object);
}