namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Channels.RoutingPolicies;
using Application.Payments.Trampoline;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;

/// <summary>
/// NL-875 TR3-T1: <see cref="TrampolineRelayPolicy"/> (TR-R-09): the fee and expiry checks of a complete set, the leg's
/// budget, and NODE|26 with our policy for the fee and the delta; NL-922: the expiry bounds of both evaluators and a
/// blinded hop's <c>payment_relay</c> against our policy for the hop.
/// </summary>
public class TrampolineRelayPolicyTests
{
    private const uint Height = 800_000;

    private static readonly TrampolineOptions s_options = new();

    // ExpiryTooSoonBlocks 18, MaxCltvExpiryDistance 2016, CltvExpiryDelta 40
    private static readonly RoutingOptions s_routing = new();

    // 1,000,000 msat out: our fee is 1000 + 1000 ppm = 2,000 msat
    private static readonly LightningMoney s_amountOut = LightningMoney.MilliSatoshis(1_000_000);
    private const uint CltvOut = Height + 100;
    private const ushort ForwardingDelta = 40;

    // Node:Routing as a channel's configured policy: 1000 msat + 1 ppm, delta 40
    private static readonly ConfiguredChannelPolicy s_policy = ConfiguredChannelPolicy.From(s_routing, null);

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

    #region Evaluate

    [Fact]
    public void Given_ASetPayingTheFeeAndDelta_When_Evaluated_Then_TheLegMayUseTheWholeDifference()
    {
        // Arrange: 2,000 msat of our fee plus 5,000 msat more
        var set = Set(1_007_000, CltvOut + 576);

        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, s_routing, set);

        // Assert: our fee and delta pay for the route (D-TR6); only our forwarding delta is kept
        Assert.True(decision.IsAccepted);
        Assert.Equal(LightningMoney.MilliSatoshis(7_000), decision.MaxFee);
        Assert.Equal(LightningMoney.MilliSatoshis(2_000), decision.OurFee);
        Assert.Equal(CltvOut + 576 - ForwardingDelta, decision.MaxFirstHopCltvExpiry);
        Assert.Null(decision.Failure);
    }

    [Fact]
    public void Given_ExactlyOurPolicy_When_Evaluated_Then_TheLegCanStillRoute()
    {
        // Act: what a payer sends after our NODE|26 (NL-875 TR5: it used to leave the leg no budget at all)
        var decision = TrampolineRelayPolicy.Evaluate(s_options, s_routing, Set(1_002_000, CltvOut + 576));

        // Assert
        Assert.True(decision.IsAccepted);
        Assert.Equal(LightningMoney.MilliSatoshis(2_000), decision.MaxFee);
        Assert.Equal(CltvOut + 576 - ForwardingDelta, decision.MaxFirstHopCltvExpiry);
    }

    [Fact]
    public void Given_AForwardingDeltaAboveOurTrampolineDelta_When_Evaluated_Then_TheTrampolineDeltaIsKept()
    {
        // Arrange
        var options = new TrampolineOptions { CltvExpiryDelta = 100, MinCltvMarginBlocks = 48 };
        var routing = new RoutingOptions { CltvExpiryDelta = 144 };

        // Act
        var decision = TrampolineRelayPolicy.Evaluate(options, routing, Set(1_010_000, CltvOut + 100));

        // Assert
        Assert.True(decision.IsAccepted);
        Assert.Equal(CltvOut, decision.MaxFirstHopCltvExpiry);
    }

    [Theory]
    [InlineData(1_001_999UL, CltvOut + 576, "fee")]
    [InlineData(999_999UL, CltvOut + 576, "fee")]
    [InlineData(1_010_000UL, CltvOut + 575, "delta")]
    [InlineData(1_010_000UL, CltvOut - 1, "delta")]
    public void Given_ASetBelowThePolicy_When_Evaluated_Then_RefusedWithOurPolicy(ulong sumInMsat, uint minCltvIn,
                                                                                 string reason)
    {
        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, s_routing, Set(sumInMsat, minCltvIn));

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

    [Theory]
    [InlineData(Height)]
    [InlineData(Height + 18)]
    public void Given_AnOutgoingExpiryWithinExpiryTooSoonBlocks_When_Evaluated_Then_TemporaryTrampolineFailure(
        uint cltvOut)
    {
        // Arrange: NL-922, expiry_too_soon is inclusive (as HtlcForwardingPolicy); the fee and delta are paid
        var set = Set(1_010_000, cltvOut + 576, cltvOut: cltvOut);

        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, s_routing, set);

        // Assert: not NODE|26, our policy is not what is missing
        Assert.False(decision.IsAccepted);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, decision.Failure!.Code);
        Assert.Contains("too soon", decision.Reason);
    }

    [Fact]
    public void Given_AnOutgoingExpiryOneBlockPastExpiryTooSoonBlocks_When_Evaluated_Then_Accepted()
    {
        // Act
        var decision = TrampolineRelayPolicy.Evaluate(s_options, s_routing,
                                                      Set(1_010_000, Height + 19 + 576, cltvOut: Height + 19));

        // Assert
        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void Given_RecipientBlindedPaths_When_TheOutgoingValueIsNearTheHeight_Then_TheirDeltaCountsForTooSoon()
    {
        // Arrange: a payer names recipient_blinded_paths with outgoing_cltv_value = height + 3 (BOLT 12 style); the
        // leg adds the paths' cltv_expiry_delta (100) to it
        var withPaths = Set(1_010_000, Height + 3 + 100 + 576, cltvOut: Height + 3) with
        {
            RecipientPathCltvExpiryDelta = 100
        };

        // Act
        var accepted = TrampolineRelayPolicy.Evaluate(s_options, s_routing, withPaths);
        var refused = TrampolineRelayPolicy.Evaluate(s_options, s_routing,
                                                     withPaths with { RecipientPathCltvExpiryDelta = 15 });

        // Assert: Height + 103 is not too soon, Height + 18 is
        Assert.True(accepted.IsAccepted);
        Assert.False(refused.IsAccepted);
        Assert.Contains("too soon", refused.Reason);
    }

    [Fact]
    public void Given_AnIncomingExpiryBeyondMaxCltvExpiryDistance_When_Evaluated_Then_TemporaryTrampolineFailure()
    {
        // Arrange: NL-922, the highest part one block beyond 2016 (the lowest within it)
        var set = Set(1_010_000, CltvOut + 576, maxCltvIn: Height + 2_017);

        // Act
        var refused = TrampolineRelayPolicy.Evaluate(s_options, s_routing, set);
        var accepted = TrampolineRelayPolicy.Evaluate(s_options, s_routing, set with { MaxCltvIn = Height + 2_016 });

        // Assert
        Assert.False(refused.IsAccepted);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, refused.Failure!.Code);
        Assert.Contains("too far", refused.Reason);
        Assert.True(accepted.IsAccepted);
    }

    [Fact]
    public void Given_AnIncomingExpiryWithinOurMargin_When_Evaluated_Then_TemporaryTrampolineFailure()
    {
        // Arrange: with a valid configuration (margin below the delta) the delta rule already keeps the margin; the
        // check still holds for a margin above the delta
        var options = new TrampolineOptions { CltvExpiryDelta = 40, MinCltvMarginBlocks = 60 };
        var routing = new RoutingOptions { ExpiryTooSoonBlocks = 1 };

        // Act
        var refused = TrampolineRelayPolicy.Evaluate(options, routing,
                                                     Set(1_010_000, Height + 45, cltvOut: Height + 5));
        var accepted = TrampolineRelayPolicy.Evaluate(options, routing,
                                                      Set(1_010_000, Height + 61, cltvOut: Height + 5));

        // Assert
        Assert.False(refused.IsAccepted);
        Assert.Contains("margin", refused.Reason);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, refused.Failure!.Code);
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

    #endregion

    #region EvaluateBlinded

    [Fact]
    public void Given_ABlindedSetBelowOurTrampolinePolicy_When_EvaluatedBlinded_Then_AcceptedWithTheWholeDifference()
    {
        // Arrange: 110 msat above the amount out and 50 blocks of delta, far below our 2,000 msat and 576 blocks (the
        // recipient's payment_relay set them, NL-895)
        var set = Set(1_000_110, CltvOut + 50);

        // Act
        var decision = TrampolineRelayPolicy.EvaluateBlinded(s_options, s_routing, set, ForwardingDelta);

        // Assert
        Assert.True(decision.IsAccepted);
        Assert.Equal(LightningMoney.MilliSatoshis(110), decision.MaxFee);
        Assert.Equal(CltvOut + 50 - ForwardingDelta, decision.MaxFirstHopCltvExpiry);
        Assert.Null(decision.Failure);
    }

    [Fact]
    public void Given_AHopDeltaBelowTheNodes_When_EvaluatedBlinded_Then_TheHopsDeltaIsKept()
    {
        // Arrange: NL-922, a channel whose setchannelpolicy delta is 34 (the node's is 40) and a payment_relay of 36
        var set = Set(1_000_110, CltvOut + 36);

        // Act
        var decision = TrampolineRelayPolicy.EvaluateBlinded(s_options, s_routing, set, 34);

        // Assert
        Assert.True(decision.IsAccepted);
        Assert.Equal(CltvOut + 36 - 34, decision.MaxFirstHopCltvExpiry);
    }

    [Theory]
    [InlineData(999_999UL, CltvOut + 50, CltvOut, Height + 60, "out")]
    [InlineData(1_000_110UL, CltvOut + 39, CltvOut, Height + 60, "hop's delta")]
    [InlineData(1_000_110UL, CltvOut - 1, CltvOut, Height + 60, "hop's delta")]
    [InlineData(1_000_110UL, CltvOut + 50, Height, Height + 60, "too soon")]
    [InlineData(1_000_110UL, Height + 18 + 50, Height + 18, Height + 18 + 50, "too soon")]
    [InlineData(1_000_110UL, CltvOut + 50, CltvOut, Height + 2_017, "too far")]
    public void Given_ABlindedSetBreakingOurSafety_When_EvaluatedBlinded_Then_InvalidOnionBlindingNeverNodeTwentySix(
        ulong sumInMsat, uint minCltvIn, uint cltvOut, uint maxCltvIn, string reason)
    {
        // Arrange
        var set = new TrampolineRelaySet(LightningMoney.MilliSatoshis(sumInMsat), minCltvIn,
                                         Math.Max(minCltvIn, maxCltvIn), s_amountOut, cltvOut, Height);

        // Act
        var decision = TrampolineRelayPolicy.EvaluateBlinded(s_options, s_routing, set, ForwardingDelta);

        // Assert
        Assert.False(decision.IsAccepted);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decision.Failure!.Code);
        Assert.Contains(reason, decision.Reason);
    }

    [Fact]
    public void Given_ABlindedSetWithinOurMargin_When_EvaluatedBlinded_Then_InvalidOnionBlinding()
    {
        // Arrange: expiry_too_soon at one block, so the margin (48) is what refuses
        var routing = new RoutingOptions { ExpiryTooSoonBlocks = 1 };

        // Act
        var decision = TrampolineRelayPolicy.EvaluateBlinded(s_options, routing,
                                                             Set(1_000_110, Height + 48, cltvOut: Height + 2), 40);

        // Assert
        Assert.False(decision.IsAccepted);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decision.Failure!.Code);
        Assert.Contains("margin", decision.Reason);
    }

    #endregion

    #region CheckBlindedHopPrice

    [Fact]
    public void Given_APaymentRelayEqualToOurPolicy_When_Checked_Then_AcceptedKeepingThePolicysDelta()
    {
        // Act
        var check = TrampolineRelayPolicy.CheckBlindedHopPrice(new BlindedPaymentRelay(40, 1, 1_000), s_policy, []);

        // Assert
        Assert.True(check.IsAccepted);
        Assert.Equal((ushort)40, check.KeptCltvExpiryDelta);
    }

    [Theory]
    [InlineData(40, 1u, 999u, "fee")]
    [InlineData(40, 0u, 1_000u, "fee")]
    [InlineData(39, 1u, 1_000u, "cltv_expiry_delta")]
    [InlineData(40, 0u, 0u, "fee")]
    public void Given_APaymentRelayBelowOurPolicy_When_Checked_Then_Refused(ushort delta, uint ppm, uint feeBase,
                                                                          string reason)
    {
        // Act: NL-922 (D-NL922-1), the free circular rebalance included
        var check = TrampolineRelayPolicy.CheckBlindedHopPrice(new BlindedPaymentRelay(delta, ppm, feeBase), s_policy,
                                                               []);

        // Assert
        Assert.False(check.IsAccepted);
        Assert.Contains(reason, check.Reason);
    }

    [Fact]
    public void Given_APaymentRelayOfAPolicyStillInGrace_When_Checked_Then_AcceptedKeepingTheMostLenientDelta()
    {
        // Arrange: the channel's policy went from 1000 msat + 1 ppm / 40 to 2000 msat + 1 ppm / 80 within the grace
        var current = s_policy with { FeeBaseMsat = 2_000, CltvExpiryDelta = 80 };

        // Act: a payment_relay built from the replaced channel_update
        var inGrace = TrampolineRelayPolicy.CheckBlindedHopPrice(new BlindedPaymentRelay(40, 1, 1_000), current,
                                                                 [s_policy]);
        var afterGrace = TrampolineRelayPolicy.CheckBlindedHopPrice(new BlindedPaymentRelay(40, 1, 1_000), current,
                                                                    []);

        // Assert: as HtlcForwardingPolicy, each check on its own and the delta the most lenient one
        Assert.True(inGrace.IsAccepted);
        Assert.Equal((ushort)40, inGrace.KeptCltvExpiryDelta);
        Assert.False(afterGrace.IsAccepted);
    }

    #endregion

    private static TrampolineRelaySet Set(ulong sumInMsat, uint minCltvIn, uint? maxCltvIn = null,
                                         uint cltvOut = CltvOut) =>
        new(LightningMoney.MilliSatoshis(sumInMsat), minCltvIn, maxCltvIn ?? minCltvIn, s_amountOut, cltvOut, Height);
}