using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Node.Options;

/// <summary>
/// NL-270: the rounds of a payment. One route when one fits; a <c>basic_mpp</c> split over our direct channels and the
/// route hints otherwise, every part with <c>total_msat</c> = the payment's amount; the fee limit, part limit and what
/// earlier failures taught (policies from <c>channel_update</c>s, avoided channels and nodes, liquidity bounds, extra
/// CLTV).
/// </summary>
public class PaymentRoutePlannerTests
{
    private const uint Height = 700;
    private const ushort FinalDelta = 40;
    private const ulong MinPart = 10_000;

    private static readonly CompactPubKey s_us = new TestNodeKeyManager(0x01).NodeId;
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;

    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);
    private static readonly ShortChannelId s_scidCd2 = new(102, 3, 1);

    private static readonly LocalChannelCandidate s_toDavid1 =
        new(new ChannelId(Enumerable.Repeat((byte)0xD1, 32).ToArray()), s_david, new ShortChannelId(200, 1, 0));

    private static readonly LocalChannelCandidate s_toDavid2 =
        new(new ChannelId(Enumerable.Repeat((byte)0xD2, 32).ToArray()), s_david, new ShortChannelId(201, 1, 0));

    private static readonly LocalChannelCandidate s_toCarol1 =
        new(new ChannelId(Enumerable.Repeat((byte)0xC1, 32).ToArray()), s_carol, new ShortChannelId(300, 1, 0));

    private static readonly LocalChannelCandidate s_toCarol2 =
        new(new ChannelId(Enumerable.Repeat((byte)0xC2, 32).ToArray()), s_carol, new ShortChannelId(301, 1, 0));

    private readonly Dictionary<ChannelId, ulong> _liquidity = [];
    private readonly RouteConstraints _constraints = new();

    private static PaymentRoutePlanner Planner() => new(Options.Create(new NodeOptions()));

    private static PaymentTarget Target(bool mpp, params IReadOnlyList<RoutingInfo>[] hints) =>
        new(s_david, Enumerable.Repeat((byte)0x11, 32).ToArray(), Enumerable.Repeat((byte)0x22, 32).ToArray(), null,
            FinalDelta, hints, null, mpp);

    private static RoutingInfo CarolHint(ShortChannelId? scid = null) => new(s_carol, scid ?? s_scidCd, 2_000, 500, 40);

    /// <summary>What a channel can send: its liquidity minus the HTLCs already planned on it.</summary>
    private ulong Sendable(ChannelId channelId, IReadOnlyList<ulong> planned)
    {
        var total = _liquidity.GetValueOrDefault(channelId);
        var used = planned.Aggregate(0UL, (sum, amount) => sum + amount);
        return used >= total ? 0 : total - used;
    }

    private PaymentPlanRequest Request(PaymentTarget target, ulong amount, ulong maxFee, int maxParts,
                                       params LocalChannelCandidate[] channels) =>
        new(target, amount, amount, maxFee, maxParts, Height, s_us, channels, Sendable, _constraints, MinPart);

    [Fact]
    public void Given_ADirectChannelThatCarriesTheAmount_When_Planned_Then_OnePartOnTheLargestChannel()
    {
        // Arrange
        _liquidity[s_toDavid1.ChannelId] = 600_000;
        _liquidity[s_toDavid2.ChannelId] = 900_000;

        // Act
        var planned = Planner().TryPlan(Request(Target(true), 500_000, 0, 16, s_toDavid1, s_toDavid2), out var parts,
                                        out var reason);

        // Assert
        Assert.True(planned, reason);
        var part = Assert.Single(parts!);
        Assert.Equal(s_toDavid2, part.Channel);
        Assert.Equal(500_000UL, part.Route.Amount.MilliSatoshi);
        Assert.Equal(500_000UL, part.Route.TotalAmount.MilliSatoshi);
        Assert.True(part.Route.Fee.IsZero);
        Assert.Equal(Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset, part.Route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_NoChannelCarriesTheAmountAndBasicMpp_When_Planned_Then_SplitOverBothWithTheTotal()
    {
        // Arrange
        _liquidity[s_toDavid1.ChannelId] = 600_000;
        _liquidity[s_toDavid2.ChannelId] = 700_000;

        // Act
        var planned = Planner().TryPlan(Request(Target(true), 1_000_000, 0, 16, s_toDavid1, s_toDavid2),
                                        out var parts, out var reason);

        // Assert: the largest channel first, the rest on the other; every part says total_msat = 1,000,000
        Assert.True(planned, reason);
        Assert.Equal(2, parts!.Count);
        Assert.Equal((s_toDavid2, 700_000UL), (parts[0].Channel, parts[0].Route.Amount.MilliSatoshi));
        Assert.Equal((s_toDavid1, 300_000UL), (parts[1].Channel, parts[1].Route.Amount.MilliSatoshi));
        Assert.All(parts, p => Assert.Equal(1_000_000UL, p.Route.TotalAmount.MilliSatoshi));
        Assert.All(parts, p => Assert.Equal(parts[0].Route.PaymentHash, p.Route.PaymentHash));
    }

    [Fact]
    public void Given_AnInvoiceWithoutBasicMpp_When_NoChannelCarriesTheAmount_Then_NotSplit()
    {
        // Arrange
        _liquidity[s_toDavid1.ChannelId] = 600_000;
        _liquidity[s_toDavid2.ChannelId] = 700_000;

        // Act
        var planned = Planner().TryPlan(Request(Target(false), 1_000_000, 0, 16, s_toDavid1, s_toDavid2), out _,
                                        out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("basic_mpp", reason);
        Assert.Contains("can send at most 700000 msat", reason);
    }

    [Fact]
    public void Given_OnePartAllowed_When_NoChannelCarriesTheAmount_Then_NotSplit()
    {
        // Arrange
        _liquidity[s_toDavid1.ChannelId] = 600_000;
        _liquidity[s_toDavid2.ChannelId] = 700_000;

        // Act
        var planned = Planner().TryPlan(Request(Target(true), 1_000_000, 0, 1, s_toDavid1, s_toDavid2), out _,
                                        out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("splitting is turned off", reason);
    }

    [Fact]
    public void Given_TheSplitNeedsMorePartsThanAllowed_When_Planned_Then_Fails()
    {
        // Arrange: three channels of 400,000 msat for 1,000,000 msat, at most two parts
        var third = new LocalChannelCandidate(new ChannelId(Enumerable.Repeat((byte)0xD3, 32).ToArray()), s_david,
                                              new ShortChannelId(202, 1, 0));
        _liquidity[s_toDavid1.ChannelId] = 400_000;
        _liquidity[s_toDavid2.ChannelId] = 400_000;
        _liquidity[third.ChannelId] = 400_000;

        // Act
        var twoParts = Planner().TryPlan(Request(Target(true), 1_000_000, 0, 2, s_toDavid1, s_toDavid2, third),
                                         out _, out var reason);
        var threeParts = Planner().TryPlan(Request(Target(true), 1_000_000, 0, 3, s_toDavid1, s_toDavid2, third),
                                           out var parts, out _);

        // Assert
        Assert.False(twoParts);
        Assert.Contains("split into 2 part(s)", reason);
        Assert.True(threeParts);
        Assert.Equal(1_000_000UL, parts!.Aggregate(0UL, (sum, p) => sum + p.Route.Amount.MilliSatoshi));
    }

    [Fact]
    public void Given_ASplitOverAHint_When_Planned_Then_EveryPartPaysItsFeeWithinTheLimit()
    {
        // Arrange: two channels to Carol, David's hint through Carol (2000 msat + 500 ppm)
        _liquidity[s_toCarol1.ChannelId] = 600_000;
        _liquidity[s_toCarol2.ChannelId] = 600_000;
        var target = Target(true, [CarolHint()]);

        // Act
        var planned = Planner().TryPlan(Request(target, 1_000_000, 10_000, 16, s_toCarol1, s_toCarol2),
                                        out var parts, out var reason);

        // Assert: the first part takes what its channel carries after Carol's fee, the second the rest
        Assert.True(planned, reason);
        Assert.Equal(2, parts!.Count);
        Assert.Equal(1_000_000UL, parts.Aggregate(0UL, (sum, p) => sum + p.Route.Amount.MilliSatoshi));
        Assert.All(parts, p => Assert.Equal(2_000 + p.Route.Amount.MilliSatoshi * 500 / 1_000_000,
                                            p.Route.Fee.MilliSatoshi));
        Assert.InRange(parts[0].Route.FirstHopAmount.MilliSatoshi, 599_990UL, 600_000UL);
        Assert.True(parts.Aggregate(0UL, (sum, p) => sum + p.Route.Fee.MilliSatoshi) <= 10_000);
    }

    [Fact]
    public void Given_TheFeeLimitIsBelowTheHintFee_When_Planned_Then_NoRouteWithTheReason()
    {
        // Arrange
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;

        // Act
        var planned = Planner().TryPlan(Request(Target(true, [CarolHint()]), 1_000_000, 2_499, 16, s_toCarol1),
                                        out _, out var reason);

        // Assert: 2000 + 500 = 2500 msat
        Assert.False(planned);
        Assert.Contains("fee 2500 msat exceeds the limit of 2499 msat", reason);
    }

    [Fact]
    public void Given_APolicyFromAChannelUpdate_When_Planned_Then_ItReplacesTheHintsPolicy()
    {
        // Arrange
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;
        _constraints.PolicyOverrides[s_scidCd] = new HintChannelPolicy(3_000, 1_000, 80, 1, ulong.MaxValue, 7);

        // Act
        var planned = Planner().TryPlan(Request(Target(true, [CarolHint()]), 1_000_000, 10_000, 16, s_toCarol1),
                                        out var parts, out _);

        // Assert
        Assert.True(planned);
        var route = Assert.Single(parts!).Route;
        Assert.Equal(3_000UL + 1_000, route.Fee.MilliSatoshi);
        Assert.Equal(Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset + 80, route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_AnAvoidedHintChannel_When_Planned_Then_TheNextHintIsUsed()
    {
        // Arrange
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;
        _constraints.ExcludedChannels.Add(s_scidCd);

        // Act
        var planned = Planner().TryPlan(Request(Target(true, [CarolHint()], [CarolHint(s_scidCd2)]), 1_000_000,
                                                10_000, 16, s_toCarol1), out var parts, out _);

        // Assert
        Assert.True(planned);
        Assert.Equal(s_scidCd2, Assert.Single(parts!).Route.Hops[0].OutgoingShortChannelId);
    }

    [Fact]
    public void Given_EveryHintThroughAnAvoidedNode_When_Planned_Then_NoRoute()
    {
        // Arrange
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;
        _constraints.ExcludedNodes.Add(s_carol);

        // Act
        var planned = Planner().TryPlan(Request(Target(true, [CarolHint()]), 1_000_000, 10_000, 16, s_toCarol1),
                                        out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains($"node {s_carol} failed earlier", reason);
    }

    [Fact]
    public void Given_AHintChannelThatLackedLiquidity_When_Planned_Then_PartsStayBelowItsBound()
    {
        // Arrange: Carol could not forward 600,000 msat over the first hint channel; a second hint exists
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;
        _constraints.BoundChannelLiquidity(s_scidCd, 600_000);

        // Act
        var planned = Planner().TryPlan(Request(Target(true, [CarolHint()], [CarolHint(s_scidCd2)]), 1_000_000,
                                                10_000, 16, s_toCarol1), out var parts, out _);

        // Assert: one part fits neither alone through the bounded channel, so the second hint carries it
        Assert.True(planned);
        Assert.Equal(s_scidCd2, Assert.Single(parts!).Route.Hops[0].OutgoingShortChannelId);
    }

    [Fact]
    public void Given_APartInFlightOverABoundedHintChannel_When_PlanningTheRest_Then_TheBoundCountsIt()
    {
        // Arrange: Carol could not forward 600,000 msat over the hint channel; a part of 400,000 msat is still in
        // flight over it, so another 300,000 msat would bring it to the bound again
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;
        _constraints.BoundChannelLiquidity(s_scidCd, 600_000);
        var target = Target(true, [CarolHint()], [CarolHint(s_scidCd2)]);
        var inFlight = HintRouteBuilder.BuildAlong([CarolHint()], target,
                                                   LightningMoney.MilliSatoshis(400_000),
                                                   Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset,
                                                   LightningMoney.MilliSatoshis(700_000));
        var request = new PaymentPlanRequest(target, 300_000, 700_000, 10_000, 16, Height, s_us, [s_toCarol1],
                                             Sendable, _constraints, MinPart,
                                             PaymentRoutePlanner.SumHintForwards([inFlight]));

        // Act
        var planned = Planner().TryPlan(request, out var parts, out var reason);
        var plannedAlone = Planner().TryPlan(request with { HintForwardsInFlightMsat = null }, out var partsAlone,
                                             out _);

        // Assert: with the part in flight counted, the rest goes over the second hint; without it, over the first
        Assert.True(planned, reason);
        Assert.Equal(s_scidCd2, Assert.Single(parts!).Route.Hops[0].OutgoingShortChannelId);
        Assert.True(plannedAlone);
        Assert.Equal(s_scidCd, Assert.Single(partsAlone!).Route.Hops[0].OutgoingShortChannelId);
    }

    [Fact]
    public void Given_APartInFlightOverABoundedHintChannel_When_OnlyThatChannelIsLeft_Then_NoPlan()
    {
        // Arrange
        _liquidity[s_toCarol1.ChannelId] = 2_000_000;
        _constraints.BoundChannelLiquidity(s_scidCd, 600_000);
        var target = Target(true, [CarolHint()]);
        var inFlight = PaymentRoutePlanner.SumHintForwards(
            [HintRouteBuilder.BuildAlong([CarolHint()], target, LightningMoney.MilliSatoshis(400_000),
                                         Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset)]);
        var request = new PaymentPlanRequest(target, 300_000, 700_000, 10_000, 16, Height, s_us, [s_toCarol1],
                                             Sendable, _constraints, MinPart, inFlight);

        // Act
        var planned = Planner().TryPlan(request, out _, out var reason);

        // Assert: neither one part nor a split fits beside the 400,000 msat in flight
        Assert.Equal(400_000UL, inFlight[s_scidCd]);
        Assert.False(planned);
        Assert.Contains($"channel {s_scidCd} could not forward 600000 msat", reason);
    }

    [Fact]
    public void Given_OurChannelRefusedAnAmount_When_Planned_Then_ItsHtlcStaysBelowTheRefusedAmount()
    {
        // Arrange
        _liquidity[s_toDavid1.ChannelId] = 1_000_000;
        _liquidity[s_toDavid2.ChannelId] = 1_000_000;
        _constraints.BoundLocalLiquidity(s_toDavid1.ChannelId, 800_000);

        // Act
        var planned = Planner().TryPlan(Request(Target(true), 900_000, 0, 16, s_toDavid1, s_toDavid2), out var parts,
                                        out _);

        // Assert
        Assert.True(planned);
        Assert.Equal(s_toDavid2, Assert.Single(parts!).Channel);
    }

    [Fact]
    public void Given_ExtraCltv_When_Planned_Then_TheFinalCltvIsHigher()
    {
        // Arrange
        _liquidity[s_toDavid1.ChannelId] = 1_000_000;
        _constraints.ExtraCltvDelta = 6;

        // Act
        Planner().TryPlan(Request(Target(true), 100_000, 0, 16, s_toDavid1), out var parts, out _);

        // Assert
        Assert.Equal(Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset + 6,
                     Assert.Single(parts!).Route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_NoChannelToThePayeeOrAHint_When_Planned_Then_NoRoute()
    {
        // Act
        var planned = Planner().TryPlan(Request(Target(true), 100_000, 0, 16), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("No route", reason);
    }
}