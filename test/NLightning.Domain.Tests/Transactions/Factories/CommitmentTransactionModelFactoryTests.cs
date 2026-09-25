using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Factories;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Protocol.Models;
using NLightning.Tests.Utils.Mocks;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.Transactions.Factories;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Enums;

public class CommitmentTransactionModelFactoryTests
{
    private static readonly CompactPubKey s_emptyCompactPubKey = new([
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00
    ]);

    [Fact]
    public void Given_ValidParameters_When_Creating_Then_ReturnsCommitmentTransactionModel()
    {
        // Given
        var emptyCompactPubKey = new CompactPubKey([
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00
        ]);
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        var commitmentKeyDerivationService = new Mock<ICommitmentKeyDerivationService>();
        var lightningSigner = new Mock<ILightningSigner>();

        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Satoshis(15_000), LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 0, false,
                                              LightningMoney.Zero, Bolt3AppendixCVectors.LocalDelay, FeatureSupport.No);
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
        {
            TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
            Index = 0,
        };
        var commitmentNumber = new CommitmentNumber(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                    Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                    sha256Mock.Object,
                                                    Bolt3AppendixCVectors.CommitmentNumber);
        var localKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                 Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes(),
                                                 Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                 Bolt3AppendixCVectors.NodeADelayedPubkey.ToBytes(),
                                                 emptyCompactPubKey, emptyCompactPubKey);
        var remoteKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(),
                                                  emptyCompactPubKey,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                  emptyCompactPubKey, emptyCompactPubKey, emptyCompactPubKey);
        var channel = new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutputInfo, true, null,
                                       null, Bolt3AppendixCVectors.Tx0ToLocalMsat, localKeySet, 1, 0,
                                       Bolt3AppendixCVectors.ToRemoteMsat, remoteKeySet, 1, emptyCompactPubKey, 0,
                                       ChannelState.V1Opening, ChannelVersion.V1);

        var factory =
            new CommitmentTransactionModelFactory(commitmentKeyDerivationService.Object, lightningSigner.Object);

        // When
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);

        // Then
        Assert.NotNull(transactionModel);
        Assert.NotNull(transactionModel.FundingOutput);
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(Bolt3AppendixCVectors.ExpectedCommitTx0ToLocalAmount, transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(Bolt3AppendixCVectors.ExpectedCommitTx0ToRemoteAmount, transactionModel.ToRemoteOutput.Amount);
    }

    [Fact]
    public void Given_ReceivedHtlc_When_CreatingLocalCommitment_Then_HtlcIsTakenFromToRemote()
    {
        // Arrange
        var receivedHtlc = new Htlc(LightningMoney.Satoshis(10_000), null!, HtlcDirection.Incoming, 500, 0, 0,
                                    Bolt3AppendixCVectors.Htlc0PaymentHash, HtlcState.Offered);
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    remoteOfferedHtlcs: [receivedHtlc]);
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);

        // Assert
        Assert.Single(transactionModel.ReceivedHtlcOutputs);
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(LightningMoney.Satoshis(7_000_000), transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(2_990_000), transactionModel.ToRemoteOutput.Amount);
    }

    [Fact]
    public void Given_OfferedHtlc_When_CreatingLocalCommitment_Then_HtlcIsTakenFromToLocal()
    {
        // Arrange
        var offeredHtlc = new Htlc(LightningMoney.Satoshis(10_000), null!, HtlcDirection.Outgoing, 500, 0, 0,
                                   Bolt3AppendixCVectors.Htlc2PaymentHash, HtlcState.Offered);
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    localOfferedHtlcs: [offeredHtlc]);
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);

        // Assert
        Assert.Single(transactionModel.OfferedHtlcOutputs);
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(LightningMoney.Satoshis(6_990_000), transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(3_000_000), transactionModel.ToRemoteOutput.Amount);
    }

    [Fact]
    public void Given_ToRemoteBelowRemoteDustButAboveHolderDust_When_CreatingLocalCommitment_Then_ToRemoteIsKept()
    {
        // Arrange
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(5_000));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);

        // Assert
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(5_000), transactionModel.ToRemoteOutput.Amount);
    }

    [Fact]
    public void Given_ToRemoteAboveRemoteDustButBelowHolderDust_When_CreatingLocalCommitment_Then_ToRemoteIsTrimmed()
    {
        // Arrange
        var channel = CreateChannel(false, LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(5_000));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);

        // Assert
        Assert.Null(transactionModel.ToRemoteOutput);
    }

    [Fact]
    public void Given_Anchors_When_CreatingLocalCommitment_Then_FunderPaysFeeAndBothAnchors()
    {
        // Arrange
        // 1124 * 253 / 1000 = 284 sat fee (rounded down) + 2 * 330 sat anchors
        var channel = CreateChannel(true, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    LightningMoney.Satoshis(253));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(284), transactionModel.Fee);
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(LightningMoney.Satoshis(7_000_000 - 284 - 660), transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(3_000_000), transactionModel.ToRemoteOutput.Amount);
    }

    [Fact]
    public void Given_Anchors_When_CreatingRemoteCommitment_Then_AnchorsUseHolderAndCounterpartyFundingKeys()
    {
        // Arrange
        var channel = CreateChannel(true, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote);

        // Assert
        Assert.NotNull(transactionModel.LocalAnchorOutput);
        Assert.Equal(Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(),
                     (byte[])transactionModel.LocalAnchorOutput.FundingPubKey);
        Assert.NotNull(transactionModel.RemoteAnchorOutput);
        Assert.Equal(Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                     (byte[])transactionModel.RemoteAnchorOutput.FundingPubKey);

        // We are the funder, which is to_remote on their commitment
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(7_000_000 - 660), transactionModel.ToRemoteOutput.Amount);
    }

    private static CommitmentTransactionModelFactory CreateFactory()
    {
        return new CommitmentTransactionModelFactory(new Mock<ICommitmentKeyDerivationService>().Object,
                                                     new Mock<ILightningSigner>().Object);
    }

    private static ChannelModel CreateChannel(bool optionAnchors, LightningMoney localDustLimit,
                                              LightningMoney remoteDustLimit, LightningMoney localBalance,
                                              LightningMoney remoteBalance, LightningMoney? feeRatePerKw = null,
                                              List<Htlc>? localOfferedHtlcs = null,
                                              List<Htlc>? remoteOfferedHtlcs = null)
    {
        var channelConfig = new ChannelConfig(LightningMoney.Zero, feeRatePerKw ?? LightningMoney.Zero,
                                              LightningMoney.Zero, localDustLimit, 0, LightningMoney.Zero, 0,
                                              optionAnchors, remoteDustLimit, Bolt3AppendixCVectors.LocalDelay,
                                              FeatureSupport.No);
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
        {
            TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
            Index = 0,
        };
        var commitmentNumber = new CommitmentNumber(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                    Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                    new FakeSha256(), Bolt3AppendixCVectors.CommitmentNumber);
        var localKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                 s_emptyCompactPubKey,
                                                 Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                 s_emptyCompactPubKey, s_emptyCompactPubKey, s_emptyCompactPubKey);
        var remoteKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(),
                                                  s_emptyCompactPubKey,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                  s_emptyCompactPubKey, s_emptyCompactPubKey, s_emptyCompactPubKey);

        return new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutputInfo, true, null, null,
                                localBalance, localKeySet, 1, 0, remoteBalance, remoteKeySet, 1, s_emptyCompactPubKey,
                                0, ChannelState.V1Opening, ChannelVersion.V1, localOfferedHtlcs,
                                remoteOfferedHtlcs: remoteOfferedHtlcs);
    }
}