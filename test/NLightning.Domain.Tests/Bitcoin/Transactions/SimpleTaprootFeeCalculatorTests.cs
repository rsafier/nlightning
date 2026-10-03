using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Bitcoin.Transactions;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Extensions;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Models;

/// <summary>
/// <see cref="CommitmentFormat.SimpleTaproot"/> in the one fee calculator (NL-231) and on the transaction models:
/// commitment weight 968 + 172 per untrimmed HTLC, zero-fee HTLC transactions, trimming on the dust limit alone, and
/// the fees of the three bolt-simple-taproot.md commitment vectors.
/// </summary>
public class SimpleTaprootFeeCalculatorTests
{
    [Theory]
    [InlineData(CommitmentFormat.StaticRemoteKey, 0, 724UL)]
    [InlineData(CommitmentFormat.Anchors, 0, 1_124UL)]
    [InlineData(CommitmentFormat.SimpleTaproot, 0, 968UL)]
    [InlineData(CommitmentFormat.SimpleTaproot, 2, 1_312UL)]
    [InlineData(CommitmentFormat.SimpleTaproot, 5, 1_828UL)]
    public void Given_AFormat_When_ComputingTheCommitmentWeight_Then_BasePlus172PerHtlc(CommitmentFormat format,
        int htlcs, ulong expected)
    {
        // Act
        var weight = CommitmentFeeCalculator.CommitmentWeight(format, htlcs);

        // Assert
        Assert.Equal(expected, weight);
    }

    [Theory]
    [InlineData(15_000UL, 0, 14_520UL)] // "simple commitment tx with no HTLCs"
    [InlineData(644UL, 5, 1_177UL)] // "commitment tx with five HTLCs untrimmed"
    [InlineData(644UL, 2, 844UL)] // "commitment tx with some HTLCs trimmed": three below the 2,500 sat dust limit
    public void Given_TheVectorsFeerates_When_ComputingTheBaseFee_Then_TheVectorsFees(ulong feeRatePerKw, int htlcs,
        ulong expectedSatoshis)
    {
        // Act
        var fee = CommitmentFeeCalculator.CommitmentBaseFee(feeRatePerKw, CommitmentFormat.SimpleTaproot, htlcs);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(expectedSatoshis), fee);
        Assert.Equal(expectedSatoshis,
                     CommitmentFeeCalculator.CommitmentBaseFeeSatoshis(feeRatePerKw, CommitmentFormat.SimpleTaproot,
                                                                       htlcs));
    }

    [Fact]
    public void Given_SimpleTaproot_When_ComputingHtlcTransactionFees_Then_ZeroAtAnyFeerate()
    {
        // Act / Assert
        Assert.Equal(LightningMoney.Zero, CommitmentFeeCalculator.HtlcTimeoutFee(100_000,
                                                                                  CommitmentFormat.SimpleTaproot));
        Assert.Equal(LightningMoney.Zero, CommitmentFeeCalculator.HtlcSuccessFee(100_000,
                                                                                  CommitmentFormat.SimpleTaproot));
        Assert.Equal(0UL, CommitmentFeeCalculator.HtlcTransactionFeeSatoshis(true, 100_000,
                                                                             CommitmentFormat.SimpleTaproot));
        Assert.Equal(0UL, CommitmentFeeCalculator.HtlcTransactionFeeSatoshis(false, 100_000,
                                                                             CommitmentFormat.SimpleTaproot));
    }

    [Theory]
    [InlineData(2_500_000UL, true, false)]
    [InlineData(2_500_000UL, false, false)]
    [InlineData(2_499_999UL, true, true)]
    [InlineData(2_499_999UL, false, true)]
    public void Given_SimpleTaproot_When_TrimmingAtAHighFeerate_Then_OnlyTheDustLimitCounts(ulong amountMsat,
        bool offered, bool trimmed)
    {
        // Act
        var result = CommitmentFeeCalculator.IsHtlcTrimmed(amountMsat, offered, 2_500, 100_000,
                                                           CommitmentFormat.SimpleTaproot);

        // Assert
        Assert.Equal(trimmed, result);
    }

    [Fact]
    public void Given_SimpleTaproot_When_ComputingTheFunderCost_Then_BaseFeePlusBothAnchors()
    {
        // Act
        var cost = CommitmentFeeCalculator.FunderCost(15_000, CommitmentFormat.SimpleTaproot, 0);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(14_520 + 660), cost);
        Assert.Equal(14_520UL + 660, CommitmentFeeCalculator.FunderCostSatoshis(15_000, CommitmentFormat.SimpleTaproot,
                                                                                0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_TheOptionAnchorsOverloads_When_Compared_Then_TheyEqualTheFormatOverloads(bool hasAnchors)
    {
        // Arrange
        var format = CommitmentFormatExtensions.FromOptionAnchors(hasAnchors);

        // Act / Assert
        Assert.Equal(CommitmentFeeCalculator.CommitmentWeight(format, 3),
                     CommitmentFeeCalculator.CommitmentWeight(hasAnchors, 3));
        Assert.Equal(CommitmentFeeCalculator.CommitmentBaseFee(2_000, format, 3),
                     CommitmentFeeCalculator.CommitmentBaseFee(2_000, hasAnchors, 3));
        Assert.Equal(CommitmentFeeCalculator.FunderCost(2_000, format, 3),
                     CommitmentFeeCalculator.FunderCost(2_000, hasAnchors, 3));
        Assert.Equal(CommitmentFeeCalculator.HtlcTimeoutFee(2_000, format),
                     CommitmentFeeCalculator.HtlcTimeoutFee(2_000, hasAnchors));
        Assert.Equal(CommitmentFeeCalculator.HtlcSuccessFee(2_000, format),
                     CommitmentFeeCalculator.HtlcSuccessFee(2_000, hasAnchors));
    }

    [Fact]
    public void Given_TheFormats_When_AskedWhatTheyImply_Then_TaprootHasAnchorOutputsAndIsTaproot()
    {
        // Act / Assert
        Assert.Equal(CommitmentFormat.StaticRemoteKey, CommitmentFormatExtensions.FromOptionAnchors(false));
        Assert.Equal(CommitmentFormat.Anchors, CommitmentFormatExtensions.FromOptionAnchors(true));
        Assert.False(CommitmentFormat.StaticRemoteKey.HasAnchorOutputs());
        Assert.True(CommitmentFormat.Anchors.HasAnchorOutputs());
        Assert.True(CommitmentFormat.SimpleTaproot.HasAnchorOutputs());
        Assert.False(CommitmentFormat.Anchors.IsTaproot());
        Assert.True(CommitmentFormat.SimpleTaproot.IsTaproot());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((CommitmentFormat)99).HasAnchorOutputs());
        Assert.Throws<ArgumentOutOfRangeException>(() => CommitmentFeeCalculator.CommitmentWeight(
                                                       (CommitmentFormat)99, 0));
    }

    [Fact]
    public void Given_TheTaprootDustLimit_When_Read_Then_354Satoshis()
    {
        // Assert: the dust threshold of a 40-byte witness program, the vectors' dust_limit_satoshis
        Assert.Equal(LightningMoney.Satoshis(354), TransactionConstants.SimpleTaprootDustLimit);
        Assert.Equal(968, WeightConstants.CommitmentWeightSimpleTaproot);
    }

    [Fact]
    public void Given_ACommitmentModel_When_TheFormatIsSimpleTaproot_Then_ItHasAnchors()
    {
        // Arrange / Act
        var legacy = CreateCommitmentModel(false, null);
        var anchors = CreateCommitmentModel(true, null);
        var taproot = CreateCommitmentModel(false, CommitmentFormat.SimpleTaproot);

        // Assert
        Assert.Equal(CommitmentFormat.StaticRemoteKey, legacy.Format);
        Assert.False(legacy.HasAnchors);
        Assert.Equal(CommitmentFormat.Anchors, anchors.Format);
        Assert.False(anchors.IsSimpleTaproot);
        Assert.Equal(CommitmentFormat.SimpleTaproot, taproot.Format);
        Assert.True(taproot.HasAnchors);
        Assert.True(taproot.IsSimpleTaproot);
    }

    [Fact]
    public void Given_AnHtlcTransactionModel_When_TheFormatDisagreesWithHasAnchors_Then_ItThrows()
    {
        // Act / Assert
        Assert.Equal(CommitmentFormat.Anchors, CreateHtlcModel(true).Format);
        Assert.Equal(CommitmentFormat.StaticRemoteKey, CreateHtlcModel(false).Format);
        Assert.True(CreateHtlcModel(true, CommitmentFormat.SimpleTaproot).IsSimpleTaproot);
        Assert.Throws<ArgumentException>(() => CreateHtlcModel(false, CommitmentFormat.SimpleTaproot));
        Assert.Throws<ArgumentException>(() => CreateHtlcModel(true, CommitmentFormat.StaticRemoteKey));
    }

    private static readonly CompactPubKey s_key = new([
        0x02, 0x79, 0xbe, 0x66, 0x7e, 0xf9, 0xdc, 0xbb, 0xac, 0x55, 0xa0, 0x62, 0x95, 0xce, 0x87, 0x0b, 0x07,
        0x02, 0x9b, 0xfc, 0xdb, 0x2d, 0xce, 0x28, 0xd9, 0x59, 0xf2, 0x81, 0x5b, 0x16, 0xf8, 0x17, 0x98
    ]);

    private static CommitmentTransactionModel CreateCommitmentModel(bool hasAnchors, CommitmentFormat? format)
    {
        var commitmentNumber = new CommitmentNumber(s_key, s_key, new FakeSha256());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000), s_key, s_key, new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20
        }, 0);
        return format is { } f
                   ? new CommitmentTransactionModel(commitmentNumber, 0, LightningMoney.Zero, fundingOutput)
                   {
                       HasAnchors = hasAnchors,
                       Format = f
                   }
                   : new CommitmentTransactionModel(commitmentNumber, 0, LightningMoney.Zero, fundingOutput)
                   {
                       HasAnchors = hasAnchors
                   };
    }

    private static HtlcTransactionModel CreateHtlcModel(bool hasAnchors, CommitmentFormat? format = null)
    {
        var htlc = new Htlc(LightningMoney.Satoshis(10_000), null!, HtlcDirection.Outgoing, 500, 0, 0, new byte[32],
                            HtlcState.Offered);
        var output = new OfferedHtlcOutputInfo(htlc, s_key, s_key, s_key);
        var model = new HtlcTransactionModel(HtlcTransactionType.Timeout, new byte[32], 0, output, hasAnchors,
                                             LightningMoney.Zero, LightningMoney.Satoshis(10_000),
                                             new BitcoinLockTime(500), new BitcoinSequence(1), s_key, s_key, 144);
        return format is { } f
                   ? new HtlcTransactionModel(HtlcTransactionType.Timeout, new byte[32], 0, output, hasAnchors,
                                              LightningMoney.Zero, LightningMoney.Satoshis(10_000),
                                              new BitcoinLockTime(500), new BitcoinSequence(1), s_key, s_key, 144)
                   {
                       Format = f
                   }
                   : model;
    }
}