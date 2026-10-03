namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Trampoline;
using Domain.Money;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// NL-875 TR3-T1: <see cref="TrampolineRelayPolicy"/> (TR-R-09): the fee and expiry checks of a complete set, the leg's
/// budget, and NODE|26 with our policy on every refusal.
/// </summary>
public class TrampolineRelayPolicyTests
{
    private const uint Height = 800_000;

    private static readonly TrampolineOptions s_options = new();

    // 1,000,000 msat out: our fee is 1000 + 1000 ppm = 2,000 msat
    private static readonly LightningMoney s_amountOut = LightningMoney.MilliSatoshis(1_000_000);
    private const uint CltvOut = Height + 100;

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_TheyAreThePlanDefaults()
    {
        // Arrange
        var options = new TrampolineOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert (D-TR6)
        Assert.Equal(1_000u, options.FeeBaseMsat);
        Assert.Equal(1_000u, options.FeeProportionalMillionths);
        Assert.Equal((ushort)576, options.CltvExpiryDelta);
        Assert.Equal(32, options.MaxRelaysInFlight);
        Assert.Equal(TimeSpan.FromSeconds(60), options.LegTimeout);
        Assert.Equal(48u, options.MinCltvMarginBlocks);
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_InvalidOptions_When_Validated_Then_EachIsReported()
    {
        // Arrange
        var options = new TrampolineOptions { CltvExpiryDelta = 40, MinCltvMarginBlocks = 40, MaxRelaysInFlight = -1 };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("MinCltvMarginBlocks"));
        Assert.Contains(errors, e => e.Contains("MaxRelaysInFlight"));
    }

    [Fact]
    public void Given_ASetPayingTheFeeAndDelta_When_Evaluated_Then_AcceptedWithTheLegBudget()
    {
        // Arrange: 2,000 msat of our fee plus 5,000 msat for the leg's routing
        var sumIn = LightningMoney.MilliSatoshis(1_007_000);
        var minCltvIn = CltvOut + 576;

        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, sumIn, minCltvIn, s_amountOut, CltvOut, Height);

        // Assert
        Assert.True(decision.IsAccepted);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000), decision.MaxFee);
        Assert.Equal(LightningMoney.MilliSatoshis(2_000), decision.OurFee);
        Assert.Equal(CltvOut, decision.MaxFirstHopCltvExpiry);
        Assert.Null(decision.Failure);
    }

    [Fact]
    public void Given_ExactlyOurFee_When_Evaluated_Then_AcceptedWithNoRoutingBudget()
    {
        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, LightningMoney.MilliSatoshis(1_002_000),
                                                      CltvOut + 600, s_amountOut, CltvOut, Height);

        // Assert
        Assert.True(decision.IsAccepted);
        Assert.Equal(LightningMoney.Zero, decision.MaxFee);
        Assert.Equal(CltvOut + 24, decision.MaxFirstHopCltvExpiry);
    }

    [Theory]
    [InlineData(1_001_999UL, CltvOut + 576, CltvOut, "fee")]
    [InlineData(999_999UL, CltvOut + 576, CltvOut, "fee")]
    [InlineData(1_010_000UL, CltvOut + 575, CltvOut, "delta")]
    [InlineData(1_010_000UL, CltvOut - 1, CltvOut, "delta")]
    [InlineData(1_010_000UL, Height + 576, Height, "height")]
    public void Given_ASetBelowThePolicy_When_Evaluated_Then_RefusedWithOurPolicy(ulong sumInMsat, uint minCltvIn,
                                                                                 uint cltvOut, string reason)
    {
        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, LightningMoney.MilliSatoshis(sumInMsat), minCltvIn,
                                                      s_amountOut, cltvOut, Height);

        // Assert: NODE|26 carries fee_base_msat, fee_proportional_millionths and cltv_expiry_delta
        Assert.False(decision.IsAccepted);
        Assert.Contains(reason, decision.Reason);
        Assert.NotNull(decision.Failure);
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, decision.Failure.Code);
        Assert.True(decision.Failure.TryGetTrampolinePolicy(out var feeBase, out var ppm, out var delta));
        Assert.Equal(s_options.FeeBaseMsat, feeBase);
        Assert.Equal(s_options.FeeProportionalMillionths, ppm);
        Assert.Equal(s_options.CltvExpiryDelta, delta);
    }

    [Fact]
    public void Given_AnIncomingExpiryWithinOurMargin_When_Evaluated_Then_Refused()
    {
        // Arrange: with a valid configuration (margin below the delta) the delta rule already keeps the margin; the
        // check still holds for a margin above the delta
        var options = new TrampolineOptions { CltvExpiryDelta = 40, MinCltvMarginBlocks = 60 };

        // Act
        var refused = TrampolineRelayPolicy.Evaluate(options, LightningMoney.MilliSatoshis(1_010_000), Height + 45,
                                                     s_amountOut, Height + 5, Height);
        var accepted = TrampolineRelayPolicy.Evaluate(options, LightningMoney.MilliSatoshis(1_010_000), Height + 61,
                                                      s_amountOut, Height + 5, Height);

        // Assert
        Assert.False(refused.IsAccepted);
        Assert.Contains("margin", refused.Reason);
        Assert.True(accepted.IsAccepted);
    }

    [Fact]
    public void Given_ALargeAmount_When_TheFeeIsComputed_Then_NoOverflow()
    {
        // Arrange: 21M BTC in msat
        var amount = LightningMoney.MilliSatoshis(2_100_000_000_000_000_000UL);

        // Act
        var fee = TrampolineRelayPolicy.FeeOf(s_options, amount);

        // Assert
        Assert.Equal(1_000UL + 2_100_000_000_000_000UL, fee.MilliSatoshi);
    }
}