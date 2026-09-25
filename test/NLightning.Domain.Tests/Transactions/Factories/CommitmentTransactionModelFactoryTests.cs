using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Factories;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Protocol.Models;
using NLightning.Tests.Utils.Mocks;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.Transactions.Factories;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
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
                                                    sha256Mock.Object);
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
                                       ChannelState.V1Opening, ChannelVersion.V1,
                                       localCommitmentNumber: Bolt3AppendixCVectors.CommitmentNumber);

        var factory =
            new CommitmentTransactionModelFactory(commitmentKeyDerivationService.Object, lightningSigner.Object);

        // When
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

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
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

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
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

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
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

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
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

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
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

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
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote,
                                                                       channel.RemoteCommitmentNumber);

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

    [Fact]
    public void Given_LocalCommitmentNumber_When_CreatingLocalCommitment_Then_KeysAreDerivedFromThatNumber()
    {
        // Arrange - regression (NL-187): the factory used to pass LocalKeySet.CurrentPerCommitmentIndex (an index)
        // where the key derivation expects a commitment number
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    localCommitmentNumber: 5);
        var keyDerivationService = new Mock<ICommitmentKeyDerivationService>();
        var factory = new CommitmentTransactionModelFactory(keyDerivationService.Object,
                                                            new Mock<ILightningSigner>().Object);

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

        // Assert
        Assert.Equal(5UL, transactionModel.Number);
        keyDerivationService.Verify(x => x.DeriveLocalCommitmentKeys(channel.LocalKeySet.KeyIndex,
                                                                     It.IsAny<ChannelBasepoints>(),
                                                                     It.IsAny<ChannelBasepoints>(), 5UL),
                                    Times.Once);
    }

    [Fact]
    public void Given_DifferentLocalAndRemoteNumbers_When_CreatingBothSides_Then_EachUsesItsOwnObscuredNumber()
    {
        // Arrange - regression (NL-188): one shared number gave one of the two commitments the wrong locktime
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    localCommitmentNumber: 3, remoteCommitmentNumber: 4);
        var factory = CreateFactory();
        const ulong obscuringFactor = 0x2bb038521914UL; // BOLT 3 Appendix C

        // Act
        var local = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                            channel.LocalCommitmentNumber);
        var remote = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote,
                                                             channel.RemoteCommitmentNumber);

        // Assert
        Assert.Equal(obscuringFactor, channel.CommitmentNumber!.ObscuringFactor);
        Assert.Equal((0x20U << 24) | (uint)((3 ^ obscuringFactor) & 0xFFFFFF), local.GetLockTime().ValueOrHeight);
        Assert.Equal((0x20U << 24) | (uint)((4 ^ obscuringFactor) & 0xFFFFFF), remote.GetLockTime().ValueOrHeight);
        Assert.Equal((0x80U << 24) | (uint)((obscuringFactor >> 24) & 0xFFFFFF), local.GetSequence().Value);
    }

    [Fact]
    public void Given_Bolt3CommitmentNumber_When_CreatingLocalCommitment_Then_LockTimeAndSequenceMatchAppendixC()
    {
        // Arrange
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       Bolt3AppendixCVectors.CommitmentNumber);

        // Assert - every Appendix C commitment tx has nLocktime 0x2052193e and nSequence 0x802bb038
        Assert.Equal(0x2052193eU, transactionModel.GetLockTime().ValueOrHeight);
        Assert.Equal(0x802bb038U, transactionModel.GetSequence().Value);
    }

    [Fact]
    public void Given_NumberAbove48Bits_When_CreatingCommitment_Then_Throws()
    {
        // Arrange
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000));
        var factory = CreateFactory();

        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateCommitmentTransactionModel(
                                                       channel, CommitmentSide.Local, 1UL << 48));
    }

    [Fact]
    public void Given_BothOutputsBelowReserve_When_CreatingCommitment_Then_TransactionIsBuilt()
    {
        // Arrange - NL-196: the reserve is an update-validation rule; Appendix C "fee greater than funder amount"
        // style commitments must still build.
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(5_000), LightningMoney.Satoshis(4_000),
                                    channelReserve: LightningMoney.Satoshis(100_000));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

        // Assert
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(LightningMoney.Satoshis(5_000), transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(4_000), transactionModel.ToRemoteOutput.Amount);
    }

    [Fact]
    public void Given_FeeGreaterThanFunderBalance_When_CreatingCommitment_Then_FunderOutputOmittedAndNoThrow()
    {
        // Arrange - funder balance 1_000 sat cannot pay 724 * 15_000 / 1000 = 10_860 sat
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(3_000_000),
                                    LightningMoney.Satoshis(15_000), channelReserve: LightningMoney.Satoshis(10_000));
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

        // Assert
        Assert.Null(transactionModel.ToLocalOutput);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(10_860), transactionModel.Fee);
    }

    [Fact]
    public void Given_AnchorsAndHtlcAtDustLimit_When_CreatingLocalCommitment_Then_HtlcIsNotTrimmed()
    {
        // Arrange - NL-195: with option_anchors the HTLC-timeout fee is 0, so 546 sat at a 546 sat dust limit stays
        // even at a high feerate (the 666 weight would trim it).
        var offeredHtlc = new Htlc(LightningMoney.Satoshis(546), null!, HtlcDirection.Outgoing, 500, 0, 0,
                                   Bolt3AppendixCVectors.Htlc2PaymentHash, HtlcState.Offered);
        var dustHtlc = new Htlc(LightningMoney.MilliSatoshis(545_999), null!, HtlcDirection.Outgoing, 501, 1, 0,
                                Bolt3AppendixCVectors.Htlc3PaymentHash, HtlcState.Offered);
        var channel = CreateChannel(true, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    LightningMoney.Satoshis(10_000), [offeredHtlc, dustHtlc]);
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

        // Assert
        var htlcOutput = Assert.Single(transactionModel.OfferedHtlcOutputs);
        Assert.Equal(0UL, htlcOutput.Htlc.Id);
        Assert.Equal(LightningMoney.Satoshis((1124 + 172) * 10_000 / 1000), transactionModel.Fee);
    }

    [Fact]
    public void Given_Spec_When_CreatingLocalCommitment_Then_ChannelBalancesAndHtlcsAreIgnored()
    {
        // Arrange - the spec's balances are already net of its HTLC; the channel's balances must not be used
        var offeredHtlc = new Htlc(LightningMoney.Satoshis(10_000), null!, HtlcDirection.Outgoing, 500, 0, 0,
                                   Bolt3AppendixCVectors.Htlc2PaymentHash, HtlcState.Offered);
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(1), LightningMoney.Satoshis(1));
        var spec = new CommitmentSpec(6_990_000_000, 3_000_000_000, 0, [offeredHtlc]);
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, spec, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

        // Assert
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(LightningMoney.Satoshis(6_990_000), transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(3_000_000), transactionModel.ToRemoteOutput.Amount);
        Assert.Single(transactionModel.OfferedHtlcOutputs);
    }

    [Fact]
    public void Given_Spec_When_CreatingRemoteCommitment_Then_BalancesAndHtlcDirectionsAreFromTheHolder()
    {
        // Arrange - we are the funder: on the remote holder's tx our balance is to_remote and pays the fee
        var outgoingHtlc = new Htlc(LightningMoney.Satoshis(10_000), null!, HtlcDirection.Outgoing, 500, 0, 0,
                                    Bolt3AppendixCVectors.Htlc2PaymentHash, HtlcState.Offered);
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Zero, LightningMoney.Zero);
        var spec = new CommitmentSpec(6_990_000_000, 3_000_000_000, 1_000, [outgoingHtlc]);
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, spec, CommitmentSide.Remote, 7,
                                                                       s_emptyCompactPubKey);

        // Assert - (724 + 172) * 1000 / 1000 = 896 sat, paid out of our (the funder's) to_remote
        Assert.Single(transactionModel.ReceivedHtlcOutputs);
        Assert.Empty(transactionModel.OfferedHtlcOutputs);
        Assert.Equal(LightningMoney.Satoshis(896), transactionModel.Fee);
        Assert.NotNull(transactionModel.ToLocalOutput);
        Assert.Equal(LightningMoney.Satoshis(3_000_000), transactionModel.ToLocalOutput.Amount);
        Assert.NotNull(transactionModel.ToRemoteOutput);
        Assert.Equal(LightningMoney.Satoshis(6_990_000 - 896), transactionModel.ToRemoteOutput.Amount);
        Assert.Equal(7UL, transactionModel.Number);
    }

    [Fact]
    public void Given_RemoteSideWithoutPoint_When_CreatingFromSpec_Then_Throws()
    {
        // Arrange
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Zero, LightningMoney.Zero);
        var factory = CreateFactory();

        // Act / Assert
        Assert.Throws<ArgumentNullException>(() => factory.CreateCommitmentTransactionModel(
                                                 channel, new CommitmentSpec(1_000_000, 1_000_000, 0),
                                                 CommitmentSide.Remote, 0));
    }

    [Fact]
    public void Given_LocalSideWithPoint_When_CreatingFromSpec_Then_Throws()
    {
        // Arrange
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Zero, LightningMoney.Zero);
        var factory = CreateFactory();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => factory.CreateCommitmentTransactionModel(
                                             channel, new CommitmentSpec(1_000_000, 1_000_000, 0),
                                             CommitmentSide.Local, 0, s_emptyCompactPubKey));
    }

    [Fact]
    public void Given_Spec_When_CreatingCommitment_Then_ModelCarriesHtlcTransactionParameters()
    {
        // Arrange
        var channel = CreateChannel(true, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Zero, LightningMoney.Zero);
        var spec = new CommitmentSpec(7_000_000_000, 3_000_000_000, 253);
        var factory = CreateFactory();

        // Act
        var transactionModel = factory.CreateCommitmentTransactionModel(channel, spec, CommitmentSide.Local,
                                                                       channel.LocalCommitmentNumber);

        // Assert
        Assert.Equal(253UL, transactionModel.FeeRatePerKw);
        Assert.True(transactionModel.HasAnchors);
        Assert.Equal(Bolt3AppendixCVectors.LocalDelay, transactionModel.ToSelfDelay);
        Assert.NotNull(transactionModel.LocalDelayedPubKey);
        Assert.NotNull(transactionModel.RevocationPubKey);
    }

    [Fact]
    public void Given_GrossChannelBalances_When_BuildingSpecFromChannel_Then_EachHtlcLeavesItsOfferersBalance()
    {
        // Arrange
        var offered = new Htlc(LightningMoney.Satoshis(2_000), null!, HtlcDirection.Outgoing, 500, 0, 0,
                               Bolt3AppendixCVectors.Htlc2PaymentHash, HtlcState.Offered);
        var received = new Htlc(LightningMoney.Satoshis(1_000), null!, HtlcDirection.Incoming, 501, 0, 0,
                                Bolt3AppendixCVectors.Htlc0PaymentHash, HtlcState.Offered);
        var channel = CreateChannel(false, LightningMoney.Satoshis(546), LightningMoney.Satoshis(546),
                                    LightningMoney.Satoshis(7_000_000), LightningMoney.Satoshis(3_000_000),
                                    LightningMoney.Satoshis(15_000), [offered], [received]);

        // Act
        var spec = CommitmentSpec.FromChannel(channel);

        // Assert
        Assert.Equal(6_998_000_000UL, spec.ToLocalMsat);
        Assert.Equal(2_999_000_000UL, spec.ToRemoteMsat);
        Assert.Equal(15_000UL, spec.FeeRatePerKw);
        Assert.Equal(2, spec.Htlcs.Count);
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
                                              List<Htlc>? remoteOfferedHtlcs = null,
                                              ulong localCommitmentNumber = Bolt3AppendixCVectors.CommitmentNumber,
                                              ulong remoteCommitmentNumber = 0,
                                              LightningMoney? channelReserve = null)
    {
        var channelConfig = new ChannelConfig(channelReserve ?? LightningMoney.Zero, feeRatePerKw ?? LightningMoney.Zero,
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
                                                    new FakeSha256());
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
                                remoteOfferedHtlcs: remoteOfferedHtlcs, localCommitmentNumber: localCommitmentNumber,
                                remoteCommitmentNumber: remoteCommitmentNumber);
    }
}