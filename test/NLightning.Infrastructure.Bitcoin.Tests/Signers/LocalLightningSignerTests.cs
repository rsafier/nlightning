using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

public class LocalLightningSignerTests
{
    [Fact]
    public void Given_ValidParameters_When_ValidatingSignature_Then_ReturnsTrue()
    {
        // Given
        var fundingOutputBuilderMock = new Mock<IFundingOutputBuilder>();
        fundingOutputBuilderMock.Setup(x => x.Build(It.IsAny<FundingOutputInfo>()))
                                .Returns(
                                     new FundingOutput(Bolt3AppendixBVectors.FundingSatoshis,
                                                       new PubKey(Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes()),
                                                       new PubKey(Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes()))
                                     {
                                         TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
                                         Index = 0,
                                     });
        var keyDerivationServiceMock = new Mock<IKeyDerivationService>();
        var loggerMock = new Mock<ILogger<LocalLightningSigner>>();
        var nodeOptions = new NodeOptions();
        var secureKeyManagerMock = new Mock<ISecureKeyManager>();
        var utxoMemoryRepository = new Mock<IUtxoMemoryRepository>();
        var testChannelId = ChannelId.Zero;
        var channelSigningInfo = new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0,
                                                        Bolt3AppendixBVectors.FundingSatoshis,
                                                        Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                        Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0);

        var localSigner = new LocalLightningSigner(fundingOutputBuilderMock.Object, keyDerivationServiceMock.Object,
                                                   loggerMock.Object, nodeOptions, secureKeyManagerMock.Object,
                                                   utxoMemoryRepository.Object);
        localSigner.RegisterChannel(testChannelId, channelSigningInfo);

        var tx = Bolt3AppendixCVectors.ExpectedCommitTx0;
        tx.Inputs[0].WitScript = null;
        var signedTx = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());

        // When
        var exception = Record
           .Exception(() => localSigner.ValidateSignature(testChannelId,
                                                          Bolt3AppendixCVectors.NodeBSignature0.ToCompact(), signedTx));

        // Then
        Assert.Null(exception);
    }

    [Fact]
    public void Given_InvalidChannelId_When_ValidatingSignature_Then_ThrowsException()
    {
        // Given
        var fundingOutputBuilderMock = new Mock<IFundingOutputBuilder>();
        var keyDerivationServiceMock = new Mock<IKeyDerivationService>();
        var loggerMock = new Mock<ILogger<LocalLightningSigner>>();
        var nodeOptions = new NodeOptions();
        var secureKeyManagerMock = new Mock<ISecureKeyManager>();
        var utxoMemoryRepository = new Mock<IUtxoMemoryRepository>();

        var localSigner = new LocalLightningSigner(fundingOutputBuilderMock.Object, keyDerivationServiceMock.Object,
                                                   loggerMock.Object, nodeOptions, secureKeyManagerMock.Object,
                                                   utxoMemoryRepository.Object);

        var unregisteredChannelId = ChannelId.Zero;
        var tx = Bolt3AppendixCVectors.ExpectedCommitTx0;
        var signedTx = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());

        // When & Then
        Assert.Throws<SignerException>(() => localSigner.ValidateSignature(
                                           unregisteredChannelId, Bolt3AppendixCVectors.NodeBSignature0.ToCompact(),
                                           signedTx));
    }

    // A fixed channel key: m/5' of it is the per-commitment seed (LocalLightningSigner.PerCommitmentSeedDerivationIndex)
    private static readonly ExtKey s_channelKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));

    private const uint ChannelKeyIndex = 7;

    [Fact]
    public void Given_Number1_When_GetPerCommitmentPoint_Then_PointAtIndexFirstMinus1()
    {
        // Given - regression (NL-187): the commitment number used to be passed through as the BOLT 3 index
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var localSigner = CreateSignerWithChannelKey(keyDerivationService);
        var expectedPoint = GetExpectedPoint(keyDerivationService, CryptoConstants.FirstPerCommitmentIndex - 1);

        // When
        var point = localSigner.GetPerCommitmentPoint(ChannelKeyIndex, 1);

        // Then
        Assert.Equal(expectedPoint, point);
    }

    [Fact]
    public void Given_RegisteredChannel_When_GetPerCommitmentPointByChannelId_Then_NumberIsConvertedToIndex()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var localSigner = CreateSignerWithChannelKey(keyDerivationService);
        var channelId = ChannelId.Zero;
        localSigner.RegisterChannel(channelId, new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0,
                                                                      Bolt3AppendixBVectors.FundingSatoshis,
                                                                      Bolt3AppendixCVectors.NodeAFundingPubkey
                                                                         .ToBytes(),
                                                                      Bolt3AppendixCVectors.NodeBFundingPubkey
                                                                         .ToBytes(),
                                                                      ChannelKeyIndex));

        // When
        var secondPoint = localSigner.GetPerCommitmentPoint(channelId, 1);
        var thirdPoint = localSigner.GetPerCommitmentPoint(channelId, 2);

        // Then
        Assert.Equal(GetExpectedPoint(keyDerivationService, CryptoConstants.FirstPerCommitmentIndex - 1), secondPoint);
        Assert.Equal(GetExpectedPoint(keyDerivationService, CryptoConstants.FirstPerCommitmentIndex - 2), thirdPoint);
    }

    [Fact]
    public void Given_NewChannel_When_GetPerCommitmentPointForNumber0_Then_EqualsFirstPerCommitmentPoint()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var localSigner = CreateSignerWithChannelKey(keyDerivationService);
        localSigner.CreateNewChannel(out _, out var firstPerCommitmentPoint);

        // When
        var point = localSigner.GetPerCommitmentPoint(ChannelKeyIndex, 0);

        // Then
        Assert.Equal(firstPerCommitmentPoint, point);
        Assert.Equal(GetExpectedPoint(keyDerivationService, CryptoConstants.FirstPerCommitmentIndex), point);
    }

    [Fact]
    public void Given_Number1_When_ReleasePerCommitmentSecret_Then_SecretAtIndexFirstMinus1MatchesPoint()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var localSigner = CreateSignerWithChannelKey(keyDerivationService);
        using var seed = s_channelKey.Derive(5, true).PrivateKey;
        var expectedSecret =
            keyDerivationService.GeneratePerCommitmentSecret(seed.ToBytes(), CryptoConstants.FirstPerCommitmentIndex - 1);

        // When
        var secret = localSigner.ReleasePerCommitmentSecret(ChannelKeyIndex, 1);

        // Then
        Assert.Equal(expectedSecret, secret);
        using var secretKey = new Key(secret);
        Assert.Equal(localSigner.GetPerCommitmentPoint(ChannelKeyIndex, 1), (CompactPubKey)secretKey.PubKey.ToBytes());
    }

    [Fact]
    public void Given_NumberAbove48Bits_When_GetPerCommitmentPoint_Then_Throws()
    {
        // Given
        var localSigner = CreateSignerWithChannelKey(new KeyDerivationService(new Secp256K1Math()));

        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => localSigner.GetPerCommitmentPoint(ChannelKeyIndex, 1UL << 48));
    }

    private static LocalLightningSigner CreateSignerWithChannelKey(IKeyDerivationService keyDerivationService)
    {
        var secureKeyManagerMock = new Mock<ISecureKeyManager>();
        secureKeyManagerMock.Setup(x => x.GetChannelKeyAtIndex(ChannelKeyIndex)).Returns(s_channelKey.ToBytes());
        var index = ChannelKeyIndex;
        secureKeyManagerMock.Setup(x => x.GetNextChannelKey(out index)).Returns(s_channelKey.ToBytes());

        return new LocalLightningSigner(new Mock<IFundingOutputBuilder>().Object, keyDerivationService,
                                        new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(),
                                        secureKeyManagerMock.Object, new Mock<IUtxoMemoryRepository>().Object);
    }

    private static CompactPubKey GetExpectedPoint(IKeyDerivationService keyDerivationService, ulong index)
    {
        using var seed = s_channelKey.Derive(5, true).PrivateKey;
        using var secretKey = new Key(keyDerivationService.GeneratePerCommitmentSecret(seed.ToBytes(), index));
        return secretKey.PubKey.ToBytes();
    }
}