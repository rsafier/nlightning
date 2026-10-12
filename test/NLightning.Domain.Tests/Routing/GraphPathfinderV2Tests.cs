namespace NLightning.Domain.Tests.Routing;

using Domain.Gossip.Graph;
using Domain.Payments.Policies;
using Domain.Routing.Pathfinding;

/// <summary>
/// Pathfinding over taproot gossip edges (NL-878): <c>channel_update_2</c> policies win over <c>channel_update</c>
/// ones, their inbound fees are charged (the draft's <c>total_fee = out.fee_base + in.inbound_base + amount ×
/// (out.prop + in.inbound_prop) / 10⁶</c>), and their staleness is by block height.
/// </summary>
public class GraphPathfinderV2Tests
{
    private readonly GraphPathfinder _pathfinder = new();

    private static GraphPolicy V2Policy(uint feeBase = 0, uint feePpm = 0, uint inboundBase = 0, uint inboundPpm = 0,
                                        uint blockHeight = 2_990, ushort cltvDelta = 40) =>
        GraphTestKit.Policy(feeBase, feePpm, cltvDelta, timestamp: blockHeight) with
        {
            GossipVersion = 2,
            InboundFeeBaseMsat = inboundBase,
            InboundFeeProportionalMillionths = inboundPpm
        };

    [Fact]
    public void Given_AnInboundFeeOnTheChannelIntoTheForwarder_When_Paying_Then_ItIsAddedToTheForwardersFee()
    {
        // Arrange: A pays C through B; B charges 1000 + 100 ppm to forward over B-C and a 500 + 1000 ppm surcharge on
        // HTLCs arriving over A-B
        var kit = new GraphTestKit();
        kit.Channel("A", "B", V2Policy(), V2Policy(inboundBase: 500, inboundPpm: 1_000));
        kit.Channel("B", "C", V2Policy(feeBase: 1_000, feePpm: 100), V2Policy());
        var graph = kit.Build();
        var request = new PathfindingRequest(kit["A"], kit["C"], 1_000_000, 18);

        // Act
        var path = _pathfinder.FindPath(graph, request);

        // Assert: out 1000 + 100, in 500 + 1000, on the 1,000,000 msat B forwards
        Assert.NotNull(path);
        Assert.Equal([kit["B"], kit["C"]], path.Hops.Select(h => h.NodeId));
        Assert.Equal(1_002_600UL, path.AmountMsat);
        Assert.Equal(2_600UL, path.Hops[0].FeeMsat);
        Assert.Equal(500u, path.Hops[0].InboundFeeBaseMsat);
        Assert.Equal(1_000u, path.Hops[0].InboundFeeProportionalMillionths);
        Assert.Equal(1_000_000UL, path.Hops[1].AmountMsat);
        Assert.Equal(0u, path.Hops[1].InboundFeeBaseMsat);
        Assert.Equal(2_600UL, path.FeeMsat);
    }

    [Fact]
    public void Given_AnInboundFee_When_TheRouteIsRebuiltFromItsRoutingInfos_Then_TheAmountsMatchThePathfinder()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("A", "B", V2Policy(), V2Policy(inboundBase: 500, inboundPpm: 1_000));
        kit.Channel("B", "C", V2Policy(feeBase: 1_000, feePpm: 100), V2Policy());
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["A"], kit["C"], 1_000_000, 18))!;

        // Act: the BOLT 11 "r" shape the planner builds its route from, the inbound fee folded into B's entry
        var infos = path.ToRoutingInfos();
        var info = Assert.Single(infos);
        var rebuilt = ForwardingFee.RequiredIncomingMsat(info.FeeBaseMsat, info.FeeProportionalMillionths,
                                                         path.ReceiverAmountMsat);

        // Assert
        Assert.Equal(1_500u, info.FeeBaseMsat);
        Assert.Equal(1_100u, info.FeeProportionalMillionths);
        Assert.Equal(path.AmountMsat, rebuilt);
    }

    [Fact]
    public void Given_AnInboundFeeThatMakesAPathDearer_When_Paying_Then_TheOtherPathIsTaken()
    {
        // Arrange: via B costs 1000 out + 2000 in, via D 1500 out and no inbound fee
        var kit = new GraphTestKit();
        kit.Channel("A", "B", V2Policy(), V2Policy(inboundBase: 2_000));
        kit.Channel("B", "C", V2Policy(feeBase: 1_000), V2Policy());
        kit.Channel("A", "D", V2Policy(), V2Policy());
        kit.Channel("D", "C", V2Policy(feeBase: 1_500), V2Policy());

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["A"], kit["C"], 1_000_000, 18));

        // Assert
        Assert.NotNull(path);
        Assert.Equal(kit["D"], path.Hops[0].NodeId);
        Assert.Equal(1_001_500UL, path.AmountMsat);
    }

    [Fact]
    public void Given_AnInboundFeeOnThePayeesChannel_When_Paying_Then_ThePayeeChargesNothing()
    {
        // Arrange: C's surcharge on HTLCs arriving over B-C applies only when C forwards
        var kit = new GraphTestKit();
        kit.Channel("A", "B", V2Policy(), V2Policy());
        kit.Channel("B", "C", V2Policy(feeBase: 1_000), V2Policy(inboundBase: 9_999));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["A"], kit["C"], 1_000_000, 18));

        // Assert
        Assert.NotNull(path);
        Assert.Equal(1_001_000UL, path.AmountMsat);
    }

    [Fact]
    public void Given_V1AndV2Policies_When_Paying_Then_TheV2PolicyIsUsed()
    {
        // Arrange: B-C has a cheap stale-looking v1 policy and the v2 policy that counts
        var kit = new GraphTestKit();
        kit.Channel("A", "B", GraphTestKit.Policy(), GraphTestKit.Policy());
        var bc = kit.Channel("B", "C", GraphTestKit.Policy(feeBase: 1), GraphTestKit.Policy());
        var graph = kit.Build();
        Assert.True(graph.TryGetChannel(bc, out var channel));
        var direction = channel.GetDirectionFrom(kit["B"]);
        var withV2 = channel.WithPolicy(GraphTestKit.WithDirection(V2Policy(feeBase: 3_000), direction));
        graph = new GraphSnapshot(graph.Channels.Select(c => c.ShortChannelId == bc ? withV2 : c), []);

        // Act
        var path = _pathfinder.FindPath(graph, new PathfindingRequest(kit["A"], kit["C"], 1_000_000, 18));

        // Assert
        Assert.NotNull(path);
        Assert.Equal(1_003_000UL, path.AmountMsat);
        Assert.True(path.Hops[1].Policy.IsV2);
    }

    [Theory]
    [InlineData(3_000u, false)]
    [InlineData(5_010u, true)]
    [InlineData(null, false)]
    public void Given_AV2EdgeDatedByBlockHeight_When_StaleChannelsAreSkipped_Then_TheTipDecides(uint? tip,
                                                                                               bool skipped)
    {
        // Arrange: B-C's v2 updates are from block 2,990, stale once the tip is above 2,990 + 2,016
        var kit = new GraphTestKit();
        kit.Channel("A", "B", GraphTestKit.Policy(), GraphTestKit.Policy());
        kit.Channel("B", "C", V2Policy(feeBase: 10), V2Policy());
        var request = new PathfindingRequest(kit["A"], kit["C"], 1_000_000, 18)
        {
            NowUnixSeconds = GraphTestKit.Timestamp + 60,
            StaleAfter = TimeSpan.FromDays(14),
            CurrentBlockHeight = tip
        };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.Equal(skipped, path is null);
    }
}