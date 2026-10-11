using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Node.Options;
using Domain.Routing.Pathfinding;

/// <summary>
/// NL-609: <see cref="PaymentRoutePlanner"/> for a payment of our own invoice (a rebalance). Every route leaves through
/// one of our channels and comes back in through another, the last hop priced with the incoming channel's peer policy;
/// without a graph only over a second channel to the same peer, with one through the graph to the incoming peer.
/// </summary>
public class CircularRoutePlannerTests
{
    private const uint Height = 700;
    private const ushort FinalDelta = 40;
    private const ulong Amount = 1_000_000;

    private static readonly CompactPubKey s_us = new TestNodeKeyManager(0x01).NodeId;
    private static readonly CompactPubKey s_carol = new TestNodeKeyManager(0x03).NodeId;
    private static readonly CompactPubKey s_david = new TestNodeKeyManager(0x04).NodeId;

    private static readonly ShortChannelId s_scidUc1 = new(300, 1, 0);
    private static readonly ShortChannelId s_scidUc2 = new(301, 1, 0);
    private static readonly ShortChannelId s_scidUd = new(302, 1, 0);
    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);

    private static readonly LocalChannelCandidate s_toCarol1 =
        new(new ChannelId(Enumerable.Repeat((byte)0xC1, 32).ToArray()), s_carol, s_scidUc1);

    private static readonly LocalChannelCandidate s_toCarol2 =
        new(new ChannelId(Enumerable.Repeat((byte)0xC2, 32).ToArray()), s_carol, s_scidUc2);

    private static readonly LocalChannelCandidate s_toDavid =
        new(new ChannelId(Enumerable.Repeat((byte)0xD1, 32).ToArray()), s_david, s_scidUd);

    private static readonly SyntheticGraph.Policy s_carolPolicy = new(2_000, 500, 40);
    private static readonly SyntheticGraph.Policy s_davidPolicy = new(1_000, 100, 30);

    private readonly RouteConstraints _constraints = new();

    private static PaymentRoutePlanner Planner() => new(Options.Create(new NodeOptions()));

    private static PaymentTarget OurInvoice(bool mpp = false) =>
        new(s_us, Enumerable.Repeat((byte)0x11, 32).ToArray(), Enumerable.Repeat((byte)0x22, 32).ToArray(), Amount,
            FinalDelta, [], null, mpp);

    /// <summary>The peer of <paramref name="channel"/> forwarding back to us over it with its policy.</summary>
    private static IncomingChannelCandidate Incoming(LocalChannelCandidate channel, uint feeBase, uint feePpm,
                                                     ushort cltvDelta, ulong htlcMaximum = ulong.MaxValue,
                                                     ulong receivable = 5_000_000) =>
        new(channel.ChannelId, channel.PeerNodeId, channel.ShortChannelId, feeBase, feePpm, cltvDelta, 1_000,
            htlcMaximum, receivable);

    private PaymentPlanRequest Request(IReadOnlyList<LocalChannelCandidate> channels,
                                       IReadOnlyList<IncomingChannelCandidate>? incoming,
                                       GraphRoutingContext? graph = null, ulong maxFee = 100_000,
                                       PaymentTarget? target = null) =>
        new(target ?? OurInvoice(), Amount, Amount, maxFee, 1, Height, s_us, channels,
            (_, planned) => 5_000_000 - planned.Aggregate(0UL, (sum, amount) => sum + amount), _constraints, 10_000,
            null, graph, incoming);

    private static GraphRoutingContext Context(GraphSnapshot graph) =>
        new(graph, new LiquidityEstimates(), new HashSet<CompactPubKey>(), 1_700_000_100);

    [Fact]
    public void Given_TwoChannelsToCarol_When_OurInvoiceIsPlanned_Then_OutOverOneAndBackOverTheOtherWithHerFee()
    {
        // Arrange: Carol forwards back to us over the second channel for 2,000 msat + 500 ppm and 40 blocks
        var incoming = Incoming(s_toCarol2, 2_000, 500, 40);

        // Act
        var planned = Planner().TryPlan(Request([s_toCarol1, s_toCarol2], [incoming]), out var parts,
                                        out var reason);

        // Assert
        Assert.True(planned, reason);
        var part = Assert.Single(parts!);
        Assert.Equal(s_toCarol1, part.Channel);
        Assert.Equal(2, part.Route.Hops.Count);
        Assert.Equal((s_carol, (ShortChannelId?)s_scidUc2),
                     (part.Route.Hops[0].NodeId, part.Route.Hops[0].OutgoingShortChannelId));
        Assert.Equal(s_us, part.Route.Hops[1].NodeId);
        Assert.Equal(Amount, part.Route.Amount.MilliSatoshi);
        Assert.Equal(2_000 + Amount * 500 / 1_000_000, part.Route.Fee.MilliSatoshi);
        Assert.Equal(Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset + 40, part.Route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_OurOnlyChannelToTheIncomingPeerIsTheIncomingOne_When_Planned_Then_NoCircularRoute()
    {
        // Arrange: a payment never leaves and comes back over the same channel
        var incoming = Incoming(s_toCarol2, 2_000, 500, 40);

        // Act
        var planned = Planner().TryPlan(Request([s_toCarol2, s_toDavid], [incoming]), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("No circular route back to us", reason);
        Assert.Contains("no other usable channel", reason);
    }

    [Fact]
    public void Given_NoIncomingChannel_When_Planned_Then_RefusedWithTheReason()
    {
        // Act
        var planned = Planner().TryPlan(Request([s_toCarol1, s_toCarol2], []), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("no channel of ours can take it back in", reason);
    }

    [Fact]
    public void Given_OurInvoiceWithoutIncomingChannels_When_Planned_Then_Throws()
    {
        // Act / Assert: a payment to ourselves needs the channels to come back through
        Assert.Throws<ArgumentException>(() => Planner().TryPlan(Request([s_toCarol1], null), out _, out _));
    }

    [Fact]
    public void Given_ATriangleInTheGraph_When_OurInvoiceIsPlanned_Then_OutToCarolThroughDavidAndBackFromDavid()
    {
        // Arrange: we -> Carol -> David -> we; David is reached from Carol over the graph
        var graph = new SyntheticGraph()
                   .Channel(s_scidUc1, s_us, s_carol, 10_000, new SyntheticGraph.Policy(0, 0, 40), s_carolPolicy)
                   .Channel(s_scidCd, s_carol, s_david, 10_000, s_carolPolicy, s_davidPolicy)
                   .Channel(s_scidUd, s_us, s_david, 10_000, new SyntheticGraph.Policy(0, 0, 40), s_davidPolicy)
                   .Build();
        var incoming = Incoming(s_toDavid, 1_000, 100, 30);

        // Act
        var planned = Planner().TryPlan(Request([s_toCarol1, s_toDavid], [incoming], Context(graph)), out var parts,
                                        out var reason);

        // Assert: never out over the incoming channel; David's then Carol's fee, rounded down (BOLT 7)
        Assert.True(planned, reason);
        var part = Assert.Single(parts!);
        Assert.Equal(s_toCarol1, part.Channel);
        Assert.Equal([s_carol, s_david, s_us], part.Route.Hops.Select(h => h.NodeId));
        Assert.Equal([s_scidCd, s_scidUd], part.Route.Hops.Take(2).Select(h => h.OutgoingShortChannelId!.Value));
        var davidFee = 1_000 + Amount * 100 / 1_000_000;
        var carolFee = 2_000 + (Amount + davidFee) * 500 / 1_000_000;
        Assert.Equal(davidFee + carolFee, part.Route.Fee.MilliSatoshi);
        Assert.Equal(Height + FinalDelta + HintRouteBuilder.FinalCltvSafetyOffset + 30 + 40,
                     part.Route.FirstHopCltvExpiry);
    }

    [Fact]
    public void Given_TheIncomingChannelFailedEarlier_When_Planned_Then_ItIsNotUsedAgain()
    {
        // Arrange
        _constraints.ExcludedChannels.Add(s_scidUc2);

        // Act
        var planned = Planner().TryPlan(Request([s_toCarol1, s_toCarol2], [Incoming(s_toCarol2, 2_000, 500, 40)]),
                                        out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("failed earlier", reason);
    }

    [Theory]
    [InlineData(500_000UL, 5_000_000UL, "forwards at most")]
    [InlineData(ulong.MaxValue, 900_000UL, "holds at most")]
    public void Given_AnIncomingChannelThatCannotCarryTheAmount_When_Planned_Then_Refused(ulong htlcMaximum,
                                                                                      ulong receivable,
                                                                                      string expected)
    {
        // Arrange
        var incoming = Incoming(s_toCarol2, 2_000, 500, 40, htlcMaximum, receivable);

        // Act
        var planned = Planner().TryPlan(Request([s_toCarol1, s_toCarol2], [incoming]), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void Given_TheFeeAboveTheLimit_When_Planned_Then_Refused()
    {
        // Act
        var planned = Planner().TryPlan(Request([s_toCarol1, s_toCarol2], [Incoming(s_toCarol2, 2_000, 500, 40)],
                                                maxFee: 1_000), out _, out var reason);

        // Assert
        Assert.False(planned);
        Assert.Contains("exceeds the limit", reason);
    }

    [Fact]
    public void Given_TwoIncomingChannelsAndBasicMpp_When_NeitherReceivesTheWholeAmount_Then_SplitOverBoth()
    {
        // Arrange: Carol can send us at most 600,000 msat back over each of two channels; we leave over the third
        var toCarol3 = new LocalChannelCandidate(new ChannelId(Enumerable.Repeat((byte)0xC3, 32).ToArray()), s_carol,
                                                 new ShortChannelId(303, 1, 0));
        var incoming1 = Incoming(s_toCarol1, 0, 0, 40, receivable: 600_000);
        var incoming2 = Incoming(s_toCarol2, 0, 0, 40, receivable: 600_000);
        var request = Request([toCarol3, s_toCarol1, s_toCarol2], [incoming1, incoming2], target: OurInvoice(true))
            with
        {
            MaxParts = 4
        };

        // Act
        var planned = Planner().TryPlan(request, out var parts, out var reason);

        // Assert: every part goes out over a channel that is not its own way back
        Assert.True(planned, reason);
        Assert.True(parts!.Count >= 2);
        Assert.Equal(Amount, parts.Aggregate(0UL, (sum, p) => sum + p.Route.Amount.MilliSatoshi));
        Assert.All(parts, p => Assert.NotEqual(p.Channel.ShortChannelId, p.Route.Hops[0].OutgoingShortChannelId));
        Assert.All(parts, p => Assert.Equal(Amount, p.Route.TotalAmount.MilliSatoshi));
    }
}