namespace NLightning.Domain.Tests.Bitcoin.Transactions;

using Domain.Bitcoin.Transactions.Factories;
using Domain.Money;

public class CommitmentFeeCalculatorTests
{
    [Fact]
    public void Given_BoltExample_Feerate5000_When_Calculating_Then_3315_3515_Base5340_Actual7140()
    {
        // Arrange - BOLT 3 "Fees > Example": feerate 5000, dust 546, offered 5000/1000 sat, received 7000/800 sat
        const ulong feeRatePerKw = 5_000;
        var dustLimit = LightningMoney.Satoshis(546);
        (LightningMoney Amount, bool Offered)[] htlcs =
        [
            (LightningMoney.MilliSatoshis(5_000_000), true),
            (LightningMoney.MilliSatoshis(1_000_000), true),
            (LightningMoney.MilliSatoshis(7_000_000), false),
            (LightningMoney.MilliSatoshis(800_000), false)
        ];

        // Act
        var timeoutFee = CommitmentFeeCalculator.HtlcTimeoutFee(feeRatePerKw, false);
        var successFee = CommitmentFeeCalculator.HtlcSuccessFee(feeRatePerKw, false);
        var trimmed = htlcs.Select(h => CommitmentFeeCalculator.IsHtlcTrimmed(h.Amount, h.Offered, dustLimit,
                                                                              feeRatePerKw, false))
                           .ToArray();
        var untrimmedCount = trimmed.Count(t => !t);
        var weight = CommitmentFeeCalculator.CommitmentWeight(false, untrimmedCount);
        var baseFee = CommitmentFeeCalculator.CommitmentBaseFee(feeRatePerKw, false, untrimmedCount);
        var actualFee = baseFee;
        for (var i = 0; i < htlcs.Length; i++)
            if (trimmed[i])
                actualFee += LightningMoney.Satoshis(htlcs[i].Amount.Satoshi);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(3_315), timeoutFee);
        Assert.Equal(LightningMoney.Satoshis(3_515), successFee);
        Assert.Equal([false, true, false, true], trimmed);
        Assert.Equal(1_068UL, weight);
        Assert.Equal(LightningMoney.Satoshis(5_340), baseFee);
        Assert.Equal(LightningMoney.Satoshis(7_140), actualFee);
    }

    [Fact]
    public void Given_Anchors_When_CalculatingHtlcFees_Then_TheyAreZero()
    {
        // Act
        var timeoutFee = CommitmentFeeCalculator.HtlcTimeoutFee(5_000, true);
        var successFee = CommitmentFeeCalculator.HtlcSuccessFee(5_000, true);

        // Assert
        Assert.Equal(LightningMoney.Zero, timeoutFee);
        Assert.Equal(LightningMoney.Zero, successFee);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_Anchors_When_HtlcAtDustLimit_Then_TrimmedOnDustLimitAlone(bool offered)
    {
        // Arrange - NL-195: zero-fee HTLC transactions, so a high feerate must not trim a 546 sat HTLC
        var dustLimit = LightningMoney.Satoshis(546);

        // Act
        var atLimit = CommitmentFeeCalculator.IsHtlcTrimmed(LightningMoney.Satoshis(546), offered, dustLimit,
                                                            100_000, true);
        var belowLimit = CommitmentFeeCalculator.IsHtlcTrimmed(LightningMoney.MilliSatoshis(545_999), offered,
                                                               dustLimit, 100_000, true);

        // Assert
        Assert.False(atLimit);
        Assert.True(belowLimit);
    }

    [Fact]
    public void Given_MsatAmount_When_CheckingTrim_Then_AmountIsRoundedDownFirst()
    {
        // Arrange - feerate 0: the threshold is the dust limit; 546_999 msat is 546 sat
        var dustLimit = LightningMoney.Satoshis(547);

        // Act
        var trimmed = CommitmentFeeCalculator.IsHtlcTrimmed(LightningMoney.MilliSatoshis(546_999), true, dustLimit,
                                                            0, false);

        // Assert
        Assert.True(trimmed);
    }

    [Fact]
    public void Given_Anchors_When_CalculatingFunderCost_Then_BaseWeight1124AndTwoAnchors()
    {
        // Act
        var baseFee = CommitmentFeeCalculator.CommitmentBaseFee(253, true, 3);
        var funderCost = CommitmentFeeCalculator.FunderCost(253, true, 3);

        // Assert - (1124 + 3 * 172) * 253 / 1000 = 414
        Assert.Equal(LightningMoney.Satoshis(414), baseFee);
        Assert.Equal(LightningMoney.Satoshis(414 + 660), funderCost);
    }

    [Fact]
    public void Given_NoAnchors_When_CalculatingFunderCost_Then_OnlyBaseFee()
    {
        // Act
        var funderCost = CommitmentFeeCalculator.FunderCost(15_000, false, 0);

        // Assert - Appendix C "simple commitment tx with no HTLCs": base fee 10860
        Assert.Equal(LightningMoney.Satoshis(10_860), funderCost);
    }

    [Fact]
    public void Given_NegativeHtlcCount_When_CalculatingWeight_Then_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CommitmentFeeCalculator.CommitmentWeight(false, -1));
    }
}