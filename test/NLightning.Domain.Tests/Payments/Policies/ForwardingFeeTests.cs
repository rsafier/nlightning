namespace NLightning.Domain.Tests.Payments.Policies;

using Domain.Money;
using Domain.Payments.Policies;

public class ForwardingFeeTests
{
    // fee_base_msat, fee_proportional_millionths, amount_to_forward, expected fee
    public static TheoryData<ulong, uint, ulong, ulong> FeeTable => new()
    {
        // BOLT 7 "HTLC Fees" worked example, A->B->C: B charges 200 + 4999999 * 2000 / 1000000 = 10199
        { 200, 2000, 4_999_999, 10_199 },
        // Same example, A->D->C: 5020398 - 4999999 = 20399 (D: 400 base + 4000 ppm)
        { 400, 4000, 4_999_999, 20_399 },
        // The other two policies of the example network
        { 100, 1000, 4_999_999, 5_099 },
        { 300, 3000, 4_999_999, 15_299 },
        // ABCD roadmap happy path: Carol (2000, 500) on X = 50,000,123, then Bob (1000, 100) on X + fee_C
        { 2_000, 500, 50_000_123, 27_000 },
        { 1_000, 100, 50_027_123, 6_002 },
        // Production defaults (1000 msat + 1 ppm)
        { 1_000, 1, 1_000_000, 1_001 },
        { 1_000, 1, 999_999, 1_000 },
        // Base only / proportional only / nothing
        { 1_000, 0, 123_456_789, 1_000 },
        { 0, 1_000_000, 42, 42 },
        { 0, 0, 5_000_000, 0 },
        { 0, 1, 0, 0 },
        // The proportional part is rounded down, never up
        { 0, 1, 999_999, 0 },
        { 0, 1, 1_000_000, 1 },
        { 0, 333, 3_003, 0 },
        { 0, 333, 3_004, 1 },
        // A product far above 2^64 still divides exactly (128-bit intermediate)
        { 0, 1_000_000, 1UL << 60, 1UL << 60 },
        { 0, uint.MaxValue, 1_000_000_000_000, 4_294_967_295_000_000 }
    };

    [Theory]
    [MemberData(nameof(FeeTable))]
    public void Given_Policy_When_CalculateMsat_Then_MatchesBolt7Formula(ulong feeBase, uint feePpm, ulong amount,
                                                                         ulong expectedFee)
    {
        // Act
        var fee = ForwardingFee.CalculateMsat(feeBase, feePpm, amount);

        // Assert
        Assert.Equal(expectedFee, fee);
    }

    [Theory]
    [MemberData(nameof(FeeTable))]
    public void Given_Policy_When_Calculate_Then_LightningMoneyMatchesMsat(ulong feeBase, uint feePpm, ulong amount,
                                                                           ulong expectedFee)
    {
        // Act
        var fee = ForwardingFee.Calculate(feeBase, feePpm, LightningMoney.MilliSatoshis(amount));

        // Assert
        Assert.Equal(expectedFee, fee.MilliSatoshi);
    }

    [Fact]
    public void Given_Bolt7Example_When_RequiredIncomingMsat_Then_EqualsTheUpdateAddAmount()
    {
        // Act
        var viaB = ForwardingFee.RequiredIncomingMsat(200, 2000, 4_999_999);
        var viaD = ForwardingFee.RequiredIncomingMsat(400, 4000, 4_999_999);

        // Assert (BOLT 7: A->B amount_msat 5010198, A->D amount_msat 5020398)
        Assert.Equal(5_010_198UL, viaB);
        Assert.Equal(5_020_398UL, viaD);
    }

    [Theory]
    [InlineData(5_010_198UL, true)]
    [InlineData(5_010_199UL, true)]
    [InlineData(5_010_197UL, false)]
    [InlineData(4_999_999UL, false)]
    [InlineData(4_999_998UL, false)]
    public void Given_IncomingAmount_When_PaysSufficientFee_Then_ExactlyFeeOrMoreIsAccepted(ulong incoming,
        bool expected)
    {
        // Act
        var result = ForwardingFee.PaysSufficientFee(200, 2000, incoming, 4_999_999);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Given_HugeAmounts_When_PaysSufficientFee_Then_DoesNotOverflow()
    {
        // Act
        var result = ForwardingFee.PaysSufficientFee(ulong.MaxValue, uint.MaxValue, ulong.MaxValue, ulong.MaxValue - 1);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_FeeAboveUlong_When_CalculateMsat_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<OverflowException>(() => ForwardingFee.CalculateMsat(ulong.MaxValue, 1_000_000, 1));
    }

    [Fact]
    public void Given_AmountPlusFeeAboveUlong_When_RequiredIncomingMsat_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<OverflowException>(() => ForwardingFee.RequiredIncomingMsat(1, 0, ulong.MaxValue));
    }
}