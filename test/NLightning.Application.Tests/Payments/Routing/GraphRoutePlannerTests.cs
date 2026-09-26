using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Routing;
using Application.Payments.Send;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Models;
using Domain.Node.Options;
using Domain.Protocol.Onion.Enums;
using Domain.Routing.Pathfinding;

/// <summary>
/// BOLT 7 plan G4-T3: <see cref="PaymentRoutePlanner"/> with a gossip graph. Our only channel goes to Carol; the graph
/// has Carol → David → Erin (the cheap path) and Carol → Frank → Erin (the dearer one). Erin is not our peer and her
/// invoice has no route hints, so only the graph reaches her.
/// </summary>
public class GraphRoutePlannerTests
{
    private const uint Height = 700;
    private const ushort FinalDelta = 40;
    private const ulong Amount = 1_000_000;

    private static readonly CompactPubKey s_us = new TestNodeKeyManager(0x01).NodeId;
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;
    private static readonly CompactPubKey s_erin = new TestNodeKeyManager(0x05).NodeId;
    private static readonly CompactPubKey s_frank = new TestNodeKeyManager(0x06).NodeId;

    private static readonly ShortChannelId s_scidUc = new(300, 1, 0);
    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);
    private static readonly ShortChannelId s_scidDe = new(102, 3, 0);
    private static readonly ShortChannelId s_scidCf = new(103, 4, 0);
    private static readonly ShortChannelId s_scidFe = new(104, 5, 0);

    private static readonly LocalChannelCandidate s_toCarol =
        new(new ChannelId(Enumerable.Repeat((byte)0xC1, 32).ToArray()), s_carol, s_scidUc);

    private static readonly SyntheticGraph.Policy s_carolPolicy = new(2_000, 500, 40);
    private static readonly SyntheticGraph.Policy s_davidPolicy = new(1_000, 100, 30);
    private static readonly SyntheticGraph.Policy s_frankPolicy = new(5_000, 1_000, 30);

    private readonly RouteConstraints _constraints = new();
    private ulong _sendable = 5_000_000;

    private static PaymentRoutePlanner Planner() => new(Options.Create(new NodeOptions()));

    private static PaymentTarget Target(bool mpp = false, params IReadOnlyList<RoutingInfo>[] hints) =>
        new(s_erin, Enumerable.Repeat((byte)0x11, 32).ToArray(), Enumerable.Repeat((byte)0x22, 32).ToArray(), null,
            FinalDelta, hints, null, mpp);

    private static SyntheticGraph Graph(SyntheticGraph.Policy? davidPolicy = null,
                                        SyntheticGraph.Policy? frankPolicy = null) =>
        new SyntheticGraph()
           .Channel(s_scidUc, s_us, s_carol, 10_000, new SyntheticGraph.Policy(0, 0, 40), s_carolPolicy)
           .Channel(s_scidCd, s_carol, s_david, 10_000, s_carolPolicy, s_davidPolicy)
           .Channel(s_scidDe, s_david, s_erin, 10_000, davidPolicy ?? s_davidPolicy, s_davidPolicy)
           .Channel(s_scidCf, s_carol, s_frank, 10_000, s_carolPolicy, s_frankPolicy)
           .Channel(s_scidFe, s_frank, s_erin, 10_000, frankPolicy ?? s_frankPolicy, s_frankPolicy);

    private static GraphRoutingContext Context(GraphSnapshot graph, LiquidityEstimates? liquidity = null,
                                               IReadOnlySet<CompactPubKey>? penalized = null, uint shadow = 0) =>
        new(graph, liquidity ?? new LiquidityEstimates(), penalized ?? new HashSet<CompactPubKey>(), 1_700_000_100)
        {
            ShadowCltvOffset = shadow
        };

    private PaymentPlanRequest Request(PaymentTarget target, GraphRoutingContext? graph, ulong maxFee = 100_000,
                                       int maxParts = 1) =>
        new(target, Amount, Amount, maxFee, maxParts, Height, s_us, [s_toCarol], (_, planned) =>
        {
            var used = planned.Aggregate(0UL, (sum, amount) => sum + amount);
            return used >= _sendable ? 0 : _sendable - used;
        }, _constraints, 10_000, null, graph);

    /// <summary>David's and Carol's fees for 1,000,000 msat to Erin over David (BOLT 7 "HTLC Fees", rounded down).</summary>
    private const ulong DavidFee = 1_000 + Amount * 100 / 1_000_000;

    private const ulong CarolFee = 2_000 + (Amount + DavidFee) * 500 / 1_000_000;

    [Fact]
    public void Given_NoHintsAndNoGraph_When_Planned_Then_NoRoute()
    {
        // Act
        var planned = Planner().TryPlan(Request(Target(), null), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains($"no usable channel to {s_erin}", reason);
        Assert.DoesNotContain("graph", reason);
    }

    [Fact]
    public void Given_NoHintsAndAGraph_When_Planned_Then_TheCheapestGraphRouteWithExactFeesAndCltvs()
    {
        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build())), out var parts,
                                        out var reason);

        // Assert: us → Carol → David → Erin; Carol charges on what she forwards to David, David on what Erin gets
        Assert.True(planned, reason);
        var part = Assert.Single(parts!);
        Assert.Equal(s_toCarol, part.Channel);
        var route = part.Route;
        var final = Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset;
        Assert.Equal(3, route.Hops.Count);
        Assert.Equal((s_carol, Amount + DavidFee, final + 30, (ShortChannelId?)s_scidCd),
                     (route.Hops[0].NodeId, route.Hops[0].AmountToForward.MilliSatoshi,
                      route.Hops[0].OutgoingCltvValue, route.Hops[0].OutgoingShortChannelId));
        Assert.Equal((s_david, Amount, final, (ShortChannelId?)s_scidDe),
                     (route.Hops[1].NodeId, route.Hops[1].AmountToForward.MilliSatoshi,
                      route.Hops[1].OutgoingCltvValue, route.Hops[1].OutgoingShortChannelId));
        Assert.Equal((s_erin, Amount, final),
                     (route.Hops[2].NodeId, route.Hops[2].AmountToForward.MilliSatoshi,
                      route.Hops[2].OutgoingCltvValue));
        Assert.True(route.Hops[2].IsFinal);
        Assert.Equal(Amount + DavidFee + CarolFee, route.FirstHopAmount.MilliSatoshi);
        Assert.Equal(1_100UL, DavidFee);
        Assert.Equal(2_500UL, CarolFee);
        Assert.Equal(final + 30 + 40, route.FirstHopCltvExpiry);
        Assert.StartsWith("graph route", part.Description);
    }

    [Fact]
    public void Given_AGraphRouteAboveTheFeeLimit_When_Planned_Then_Refused()
    {
        // Act: the cheapest route costs 3,600 msat
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build()), maxFee: 3_599), out _,
                                        out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("graph has no path", reason);
    }

    [Fact]
    public void Given_TheFeeLimitOfTheCheapestRoute_When_Planned_Then_ItIsUsedAtExactlyThatFee()
    {
        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build()), maxFee: 3_600), out var parts,
                                        out var reason);

        // Assert
        Assert.True(planned, reason);
        Assert.Equal(3_600UL, Assert.Single(parts!).Route.Fee.MilliSatoshi);
    }

    [Fact]
    public void Given_NoPathCarriesTheAmountAndBasicMpp_When_Planned_Then_SplitOverDiversePaths()
    {
        // Arrange: David and Frank each forward at most 600,000 msat to Erin
        var graph = Graph(s_davidPolicy with { HtlcMaximumMsat = 600_000 },
                          s_frankPolicy with { HtlcMaximumMsat = 600_000 }).Build();

        // Act
        var planned = Planner().TryPlan(Request(Target(mpp: true), Context(graph), maxParts: 4), out var parts,
                                        out var reason);

        // Assert: two parts, one over each of Erin's channels, together the amount, each with total_msat
        Assert.True(planned, reason);
        Assert.Equal(2, parts!.Count);
        Assert.Equal(Amount, parts.Aggregate(0UL, (sum, p) => sum + p.Route.Amount.MilliSatoshi));
        Assert.All(parts, p => Assert.Equal(Amount, p.Route.TotalAmount.MilliSatoshi));
        Assert.All(parts, p => Assert.True(p.Route.Amount.MilliSatoshi <= 600_000));
        var lastChannels = parts.Select(p => p.Route.Hops[^2].OutgoingShortChannelId!.Value).ToHashSet();
        Assert.Equal(new HashSet<ShortChannelId> { s_scidDe, s_scidFe }, lastChannels);
    }

    [Fact]
    public void Given_NoPathCarriesTheAmountAndNoBasicMpp_When_Planned_Then_NotSplit()
    {
        // Arrange
        var graph = Graph(s_davidPolicy with { HtlcMaximumMsat = 600_000 },
                          s_frankPolicy with { HtlcMaximumMsat = 600_000 }).Build();

        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(graph), maxParts: 4), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("graph has no path", reason);
    }

    [Fact]
    public void Given_MissionControlLearntDavidCannotForward_When_Planned_Then_TheRouteGoesOverFrank()
    {
        // Arrange: a temporary_channel_failure from David for this amount, recorded by mission control
        var missionControl = new MissionControl(Options.Create(new PaymentSendOptions()), TimeProvider.System);
        Assert.True(Planner().TryPlan(Request(Target(), Context(Graph().Build())), out var first, out _));
        missionControl.RecordFailure(first![0].Route, 1, FailureCode.TemporaryChannelFailure);
        var snapshot = missionControl.GetSnapshot();

        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build(), snapshot.Liquidity)),
                                        out var parts, out var reason);

        // Assert
        Assert.True(planned, reason);
        Assert.Equal(s_scidDe, first![0].Route.Hops[1].OutgoingShortChannelId);
        Assert.Equal(s_frank, Assert.Single(parts!).Route.Hops[1].NodeId);
        Assert.Equal(s_scidFe, parts![0].Route.Hops[1].OutgoingShortChannelId);
    }

    [Fact]
    public void Given_ANodePenalizedByMissionControl_When_Planned_Then_ItIsAvoided()
    {
        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build(), penalized: new HashSet<CompactPubKey>
                                                           {
                                                               s_david
                                                           })), out var parts, out var reason);

        // Assert
        Assert.True(planned, reason);
        Assert.Equal(s_frank, Assert.Single(parts!).Route.Hops[1].NodeId);
    }

    [Fact]
    public void Given_AChannelTheFailedAttemptExcluded_When_Planned_Then_TheOtherRouteIsUsed()
    {
        // Arrange
        _constraints.ExcludedChannels.Add(s_scidDe);

        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build())), out var parts, out var reason);

        // Assert
        Assert.True(planned, reason);
        Assert.Equal(s_scidFe, Assert.Single(parts!).Route.Hops[1].OutgoingShortChannelId);
    }

    [Fact]
    public void Given_AFailureChannelUpdate_When_Planned_Then_ItsPolicyIsUsedAndTheGraphIsUnchanged()
    {
        // Arrange: David's update from an UPDATE failure raises his base fee to 1,500 msat, for this payment only
        var graph = Graph().Build();
        var direction = DirectedChannel.Between(s_scidDe, s_david, s_erin);
        _constraints.GraphPolicyOverrides[direction] =
            new GraphPolicy(1_700_000_050, 1, direction.Direction, 30, 1, 5_000_000, 1_500, 100);

        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(graph)), out var parts, out var reason);

        // Assert
        Assert.True(planned, reason);
        var route = Assert.Single(parts!).Route;
        const ulong davidFee = 1_500 + Amount * 100 / 1_000_000;
        Assert.Equal(Amount + davidFee, route.Hops[0].AmountToForward.MilliSatoshi);
        Assert.True(graph.TryGetChannel(s_scidDe, out var stored));
        Assert.Equal(1_000u, stored.GetPolicy(direction.Direction)!.FeeBaseMsat);
    }

    [Fact]
    public void Given_APrivatePayeeWithAHintFromDavid_When_Planned_Then_GraphToDavidThenTheHint()
    {
        // Arrange: Erin is not in the graph; her invoice hints David's private channel to her
        var graph = new SyntheticGraph()
                   .Channel(s_scidUc, s_us, s_carol, 10_000, new SyntheticGraph.Policy(0, 0, 40), s_carolPolicy)
                   .Channel(s_scidCd, s_carol, s_david, 10_000, s_carolPolicy, s_davidPolicy)
                   .Build();
        var hintScid = new ShortChannelId(900, 1, 1);
        var target = Target(false, [new RoutingInfo(s_david, hintScid, 700, 200, 50)]);

        // Act
        var planned = Planner().TryPlan(Request(target, Context(graph)), out var parts, out var reason);

        // Assert
        Assert.True(planned, reason);
        var route = Assert.Single(parts!).Route;
        const ulong davidFee = 700 + Amount * 200 / 1_000_000;
        var final = Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset;
        Assert.Equal([s_carol, s_david, s_erin], route.Hops.Select(h => h.NodeId));
        Assert.Equal(hintScid, route.Hops[1].OutgoingShortChannelId);
        Assert.Equal(Amount + davidFee, route.Hops[0].AmountToForward.MilliSatoshi);
        Assert.Equal(final + 50, route.Hops[0].OutgoingCltvValue);
        Assert.Equal(final + 50 + 40, route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_AShadowOffset_When_Planned_Then_ThePayeeCltvCarriesIt()
    {
        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build(), shadow: 25)), out var parts,
                                        out var reason);

        // Assert
        Assert.True(planned, reason);
        var route = Assert.Single(parts!).Route;
        var final = Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset + 25;
        Assert.Equal(final, route.Hops[^1].OutgoingCltvValue);
        Assert.Equal(final + 30 + 40, route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_OurChannelCannotSendTheAmount_When_Planned_Then_NoGraphRoute()
    {
        // Arrange
        _sendable = 900_000;

        // Act
        var planned = Planner().TryPlan(Request(Target(), Context(Graph().Build())), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("graph has no path", reason);
    }

    [Fact]
    public void Given_ADirectChannelToThePayee_When_PlannedWithAGraph_Then_TheDirectRouteWins()
    {
        // Arrange: Carol is our peer and the payee
        var target = new PaymentTarget(s_carol, Enumerable.Repeat((byte)0x11, 32).ToArray(),
                                       Enumerable.Repeat((byte)0x22, 32).ToArray(), null, FinalDelta, []);

        // Act
        var planned = Planner().TryPlan(Request(target, Context(Graph().Build())), out var parts, out var reason);

        // Assert
        Assert.True(planned, reason);
        var part = Assert.Single(parts!);
        Assert.Equal("direct over " + s_scidUc, part.Description);
        Assert.True(part.Route.Fee.IsZero);
    }
}