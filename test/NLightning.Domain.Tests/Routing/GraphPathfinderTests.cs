using System.Diagnostics;

namespace NLightning.Domain.Tests.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Payments.Policies;
using Domain.Routing.Pathfinding;

public class GraphPathfinderTests
{
    private readonly GraphPathfinder _pathfinder = new();

    /// <summary>
    /// The four nodes of BOLT 7 "Routing Example": A-B, A-D, B-C, D-C, each node charging its listed fee and delta.
    /// </summary>
    private static (GraphTestKit Kit, GraphSnapshot Graph) Bolt7Example()
    {
        var kit = new GraphTestKit();
        var a = GraphTestKit.Policy(100, 1000, 10);
        var b = GraphTestKit.Policy(200, 2000, 20);
        var c = GraphTestKit.Policy(300, 3000, 30);
        var d = GraphTestKit.Policy(400, 4000, 40);
        kit.Channel("A", "B", a, b);
        kit.Channel("A", "D", a, d);
        kit.Channel("B", "C", b, c);
        kit.Channel("D", "C", d, c);
        return (kit, kit.Build());
    }

    [Fact]
    public void Given_Bolt7RoutingExample_When_APaysCViaB_Then_AmountsAndCltvMatchTheSpec()
    {
        // Arrange
        var (kit, graph) = Bolt7Example();
        var request = new PathfindingRequest(kit["A"], kit["C"], 4_999_999, 18) { ShadowCltvOffset = 42 };

        // Act
        var path = _pathfinder.FindPath(graph, request);

        // Assert: "A->B's update_add_htlc: amount_msat 5010198, cltv_expiry current-block-height + 20 + 18 + 42"
        Assert.NotNull(path);
        Assert.Equal(2, path.Hops.Count);
        Assert.Equal(kit["B"], path.Hops[0].NodeId);
        Assert.Equal(5_010_198UL, path.Hops[0].AmountMsat);
        Assert.Equal(20u + 18 + 42, path.Hops[0].CltvDelta);
        Assert.Equal(10_199UL, path.Hops[0].FeeMsat);
        Assert.Equal(kit["C"], path.Hops[1].NodeId);
        Assert.Equal(4_999_999UL, path.Hops[1].AmountMsat);
        Assert.Equal(18u + 42, path.Hops[1].CltvDelta);
        Assert.Equal(0UL, path.Hops[1].FeeMsat);
        Assert.Equal(10_199UL, path.FeeMsat);
        Assert.Equal(80u, path.TotalCltvDelta);
        Assert.Equal(42u, path.ShadowCltvOffset);
    }

    [Fact]
    public void Given_Bolt7RoutingExample_When_BExcluded_Then_ViaDMatchesTheSpec()
    {
        // Arrange
        var (kit, graph) = Bolt7Example();
        var request = new PathfindingRequest(kit["A"], kit["C"], 4_999_999, 18)
        {
            ShadowCltvOffset = 42,
            ExcludedNodes = new HashSet<CompactPubKey> { kit["B"] }
        };

        // Act
        var path = _pathfinder.FindPath(graph, request);

        // Assert: "A->D's update_add_htlc: amount_msat 5020398, cltv_expiry current-block-height + 40 + 18 + 42"
        Assert.NotNull(path);
        Assert.Equal([kit["D"], kit["C"]], path.Hops.Select(h => h.NodeId));
        Assert.Equal(5_020_398UL, path.AmountMsat);
        Assert.Equal(100u, path.TotalCltvDelta);
    }

    [Fact]
    public void Given_Bolt7RoutingExample_When_BPaysCDirectly_Then_NoFeeAndFinalCltv()
    {
        // Arrange
        var (kit, graph) = Bolt7Example();
        var request = new PathfindingRequest(kit["B"], kit["C"], 4_999_999, 18) { ShadowCltvOffset = 42 };

        // Act
        var path = _pathfinder.FindPath(graph, request);

        // Assert
        Assert.NotNull(path);
        Assert.Single(path.Hops);
        Assert.Equal(4_999_999UL, path.AmountMsat);
        Assert.Equal(60u, path.TotalCltvDelta);
        Assert.Empty(path.ToRoutingInfos());
    }

    [Fact]
    public void Given_Path_When_ToRoutingInfos_Then_EachIntermediateCarriesItsOutgoingPolicy()
    {
        // Arrange
        var (kit, graph) = Bolt7Example();
        var request = new PathfindingRequest(kit["A"], kit["C"], 4_999_999, 18);
        var bToC = graph.GetAdjacency(IndexOf(graph, kit["B"])).Single(a => a.NeighborIndex == IndexOf(graph, kit["C"]))
                        .Channel.ShortChannelId;

        // Act
        var infos = _pathfinder.FindPath(graph, request)!.ToRoutingInfos();

        // Assert: the BOLT 11 "r" shape HintRouteBuilder.BuildAlong takes
        var info = Assert.Single(infos);
        Assert.Equal(kit["B"], info.CompactPubKey);
        Assert.Equal(bToC, info.ShortChannelId);
        Assert.Equal(200u, info.FeeBaseMsat);
        Assert.Equal(2000u, info.FeeProportionalMillionths);
        Assert.Equal((ushort)20, info.CltvExpiryDelta);
    }

    [Fact]
    public void Given_FourHopPath_When_Found_Then_EveryHopAmountAndCltvIsExact()
    {
        // Arrange: S -> X1 -> X2 -> X3 -> T, fees applied backward from T
        var kit = new GraphTestKit();
        kit.Channel("S", "X1", GraphTestKit.Policy(5, 5, 5));
        kit.Channel("X1", "X2", GraphTestKit.Policy(1000, 100, 40));
        kit.Channel("X2", "X3", GraphTestKit.Policy(0, 2500, 144));
        kit.Channel("X3", "T", GraphTestKit.Policy(1, 1, 18));
        const ulong amount = 123_456_789;
        var request = new PathfindingRequest(kit["S"], kit["T"], amount, 22);

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.NotNull(path);
        var atX3 = ForwardingFee.RequiredIncomingMsat(1, 1, amount);
        var atX2 = ForwardingFee.RequiredIncomingMsat(0, 2500, atX3);
        var atX1 = ForwardingFee.RequiredIncomingMsat(1000, 100, atX2);
        Assert.Equal([atX1, atX2, atX3, amount], path.Hops.Select(h => h.AmountMsat));
        Assert.Equal([22u + 18 + 144 + 40, 22u + 18 + 144, 22u + 18, 22u], path.Hops.Select(h => h.CltvDelta));
        Assert.Equal([atX1 - atX2, atX2 - atX3, atX3 - amount, 0UL], path.Hops.Select(h => h.FeeMsat));
        Assert.Equal(atX1 - amount, path.FeeMsat);
    }

    [Fact]
    public void Given_TwoRoutes_When_OneIsCheaper_Then_ChoosesTheCheaperOne()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "Cheap", GraphTestKit.Policy());
        kit.Channel("Cheap", "T", GraphTestKit.Policy(1_000, 100));
        kit.Channel("S", "Dear", GraphTestKit.Policy());
        kit.Channel("Dear", "T", GraphTestKit.Policy(50_000, 5_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18));

        // Assert
        Assert.Equal(kit["Cheap"], path!.Hops[0].NodeId);
    }

    [Fact]
    public void Given_CheapRouteHtlcMaxBelowAmount_When_Finding_Then_UsesTheOtherRoute()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "Cheap", GraphTestKit.Policy());
        kit.Channel("Cheap", "T", GraphTestKit.Policy(1_000, 100, htlcMax: 999_999));
        kit.Channel("S", "Dear", GraphTestKit.Policy());
        kit.Channel("Dear", "T", GraphTestKit.Policy(50_000, 5_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18));

        // Assert
        Assert.Equal(kit["Dear"], path!.Hops[0].NodeId);
    }

    [Fact]
    public void Given_HtlcMaxEqualToAmount_When_Finding_Then_TheEdgeIsUsable()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(htlcMax: 1_000_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18));

        // Assert
        Assert.NotNull(path);
    }

    [Fact]
    public void Given_CheapRouteHtlcMinAboveAmount_When_Finding_Then_UsesTheOtherRoute()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "Cheap", GraphTestKit.Policy());
        kit.Channel("Cheap", "T", GraphTestKit.Policy(1_000, 100, htlcMin: 1_000_001));
        kit.Channel("S", "Dear", GraphTestKit.Policy());
        kit.Channel("Dear", "T", GraphTestKit.Policy(50_000, 5_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18));

        // Assert
        Assert.Equal(kit["Dear"], path!.Hops[0].NodeId);
    }

    [Fact]
    public void Given_HtlcMinOnFirstIntermediateEdge_When_AmountWithFeesClearsIt_Then_UsesIt()
    {
        // Arrange: the S -> X HTLC carries amount + X's fee, which clears the minimum; T's HTLC does not
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy(htlcMin: 1_000_500));
        kit.Channel("X", "T", GraphTestKit.Policy(1_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18));

        // Assert
        Assert.NotNull(path);
        Assert.Equal(1_001_000UL, path.AmountMsat);
    }

    [Fact]
    public void Given_CheapRouteDisabled_When_Finding_Then_UsesTheOtherRoute()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "Cheap", GraphTestKit.Policy());
        kit.Channel("Cheap", "T", GraphTestKit.Policy(1_000, 100, disabled: true));
        kit.Channel("S", "Dear", GraphTestKit.Policy());
        kit.Channel("Dear", "T", GraphTestKit.Policy(50_000, 5_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18));

        // Assert
        Assert.Equal(kit["Dear"], path!.Hops[0].NodeId);
    }

    [Fact]
    public void Given_OnlyReverseDirectionDisabled_When_Finding_Then_ForwardDirectionIsUsed()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(), GraphTestKit.Policy(disabled: true));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.NotNull(path);
    }

    [Fact]
    public void Given_NoPolicyInOurDirection_When_Finding_Then_NoPath()
    {
        // Arrange: only T announced a policy (T -> S)
        var kit = new GraphTestKit();
        kit.Channel("S", "T", null, GraphTestKit.Policy());

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void Given_CltvLimit_When_ShortRouteExceedsIt_Then_UsesTheLowerDeltaRoute()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "Slow", GraphTestKit.Policy());
        kit.Channel("Slow", "T", GraphTestKit.Policy(cltvDelta: 2000));
        kit.Channel("S", "Fast", GraphTestKit.Policy());
        kit.Channel("Fast", "T", GraphTestKit.Policy(5_000, 5_000, 40));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18) { MaxTotalCltvDelta = 2000 };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.Equal(kit["Fast"], path!.Hops[0].NodeId);
        Assert.Equal(58u, path.TotalCltvDelta);
    }

    [Fact]
    public void Given_TotalCltvExactlyAtLimit_When_Finding_Then_Allowed()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(cltvDelta: 82));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18) { MaxTotalCltvDelta = 100 };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.Equal(100u, path!.TotalCltvDelta);
        Assert.Null(_pathfinder.FindPath(kit.Build(), request with { MaxTotalCltvDelta = 99 }));
    }

    [Fact]
    public void Given_ShadowOffset_When_ItWouldExceedTheLimit_Then_ItIsCut()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(cltvDelta: 40));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18)
        {
            MaxTotalCltvDelta = 100,
            ShadowCltvOffset = 144
        };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert: 18 + 40 = 58, so at most 42 of shadow
        Assert.Equal(42u, path!.ShadowCltvOffset);
        Assert.Equal(100u, path.TotalCltvDelta);
        Assert.Equal(60u, path.Hops[^1].CltvDelta);
    }

    [Fact]
    public void Given_MaxHops_When_OnlyALongerPathExists_Then_NoPath()
    {
        // Arrange: S -> A -> B -> T is 3 channels
        var kit = new GraphTestKit();
        kit.Channel("S", "A", GraphTestKit.Policy());
        kit.Channel("A", "B", GraphTestKit.Policy());
        kit.Channel("B", "T", GraphTestKit.Policy());
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18);

        // Act / Assert
        Assert.NotNull(_pathfinder.FindPath(kit.Build(), request with { MaxHops = 3 }));
        Assert.Null(_pathfinder.FindPath(kit.Build(), request with { MaxHops = 2 }));
    }

    [Fact]
    public void Given_MaxHops_When_TheCheapestLabelIsTooLong_Then_TheShorterCostlierPathIsFound()
    {
        // Arrange: M reaches T through a free 3-channel chain (cheapest) or one 5,000 msat channel; S -> M
        var kit = new GraphTestKit();
        kit.Channel("S", "M", GraphTestKit.Policy());
        kit.Channel("M", "P2", GraphTestKit.Policy());
        kit.Channel("P2", "P1", GraphTestKit.Policy());
        kit.Channel("P1", "T", GraphTestKit.Policy());
        kit.Channel("M", "T", GraphTestKit.Policy(5_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18);

        // Act
        var unlimited = _pathfinder.FindPath(kit.Build(), request);
        var limited = _pathfinder.FindPath(kit.Build(), request with { MaxHops = 3 });

        // Assert: without the limit the chain wins; with it, S -> M -> T (2 hops) still exists and is found
        Assert.Equal(4, unlimited!.Hops.Count);
        Assert.NotNull(limited);
        Assert.Equal([kit["M"], kit["T"]], limited.Hops.Select(h => h.NodeId));
        Assert.Equal(5_000UL, limited.FeeMsat);
    }

    [Fact]
    public void Given_CltvLimit_When_TheCheapestLabelExceedsItUpstream_Then_TheLowerCltvPathIsFound()
    {
        // Arrange: M reaches T through X (free, 1500 blocks, cheapest) or Y (30,000 msat, 40 blocks); S -> N -> M
        var kit = new GraphTestKit();
        kit.Channel("S", "N", GraphTestKit.Policy());
        kit.Channel("N", "M", GraphTestKit.Policy());
        kit.Channel("M", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(cltvDelta: 1_500));
        kit.Channel("M", "Y", GraphTestKit.Policy());
        kit.Channel("Y", "T", GraphTestKit.Policy(30_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18);

        // Act: through X, M needs 18 + 1500 + 40 = 1558 (fits) and N 1598 (does not)
        var unlimited = _pathfinder.FindPath(kit.Build(), request);
        var limited = _pathfinder.FindPath(kit.Build(), request with { MaxTotalCltvDelta = 1_560 });

        // Assert
        Assert.Equal(kit["X"], unlimited!.Hops[2].NodeId);
        Assert.NotNull(limited);
        Assert.Equal(kit["Y"], limited.Hops[2].NodeId);
        Assert.Equal(18u + 40 + 40 + 40, limited.TotalCltvDelta);
    }

    [Fact]
    public void Given_FeeLimit_When_TheCheapestLabelExceedsItUpstream_Then_TheLowerFeePathIsFound()
    {
        // Arrange: M reaches T through X (100 msat, 1500 blocks) or Y (5,000 msat, 40 blocks, cheapest by cost);
        // S -> N (charges 1,000 msat) -> M
        var kit = new GraphTestKit();
        kit.Channel("S", "N", GraphTestKit.Policy());
        kit.Channel("N", "M", GraphTestKit.Policy(1_000));
        kit.Channel("M", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(100, cltvDelta: 1_500));
        kit.Channel("M", "Y", GraphTestKit.Policy());
        kit.Channel("Y", "T", GraphTestKit.Policy(5_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18);

        // Act: through Y the fee is 5,000 at M (fits) and 6,000 at N (does not)
        var unlimited = _pathfinder.FindPath(kit.Build(), request);
        var limited = _pathfinder.FindPath(kit.Build(), request with { MaxFeeMsat = 5_500 });

        // Assert
        Assert.Equal(kit["Y"], unlimited!.Hops[2].NodeId);
        Assert.NotNull(limited);
        Assert.Equal(kit["X"], limited.Hops[2].NodeId);
        Assert.Equal(1_100UL, limited.FeeMsat);
    }

    [Fact]
    public void Given_MaxFee_When_OnlyRouteCostsMore_Then_NoPath()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(1_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18);

        // Act / Assert
        Assert.NotNull(_pathfinder.FindPath(kit.Build(), request with { MaxFeeMsat = 1_000 }));
        Assert.Null(_pathfinder.FindPath(kit.Build(), request with { MaxFeeMsat = 999 }));
    }

    [Fact]
    public void Given_IntermediateWithUnknownEvenFeatures_When_Finding_Then_AvoidsIt()
    {
        // Arrange: bit 200 (even, unknown)
        var kit = new GraphTestKit();
        kit.Channel("S", "Odd", GraphTestKit.Policy());
        kit.Channel("Odd", "T", GraphTestKit.Policy());
        kit.Channel("S", "Ok", GraphTestKit.Policy());
        kit.Channel("Ok", "T", GraphTestKit.Policy(10_000));
        kit.Announce("Odd", FeatureBits(200));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.Equal(kit["Ok"], path!.Hops[0].NodeId);
    }

    [Fact]
    public void Given_IntermediateWithUnknownOddFeatures_When_Finding_Then_UsesIt()
    {
        // Arrange: bit 201 (odd, unknown): fine
        var kit = new GraphTestKit();
        kit.Channel("S", "Odd", GraphTestKit.Policy());
        kit.Channel("Odd", "T", GraphTestKit.Policy());
        kit.Announce("Odd", FeatureBits(201));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.NotNull(path);
    }

    [Fact]
    public void Given_TargetWithUnknownEvenFeatures_When_Finding_Then_NoPathUnlessAllowed()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy());
        kit.Announce("T", FeatureBits(200));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18);

        // Act / Assert
        Assert.Null(_pathfinder.FindPath(kit.Build(), request));
        Assert.NotNull(_pathfinder.FindPath(kit.Build(), request with { AllowTargetUnknownFeatures = true }));
    }

    [Fact]
    public void Given_ChannelWithUnknownEvenFeatures_When_Finding_Then_AvoidsIt()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(), features: FeatureBits(200));
        kit.Channel("S", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(5_000));

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.Equal(2, path!.Hops.Count);
    }

    [Fact]
    public void Given_SpentChannel_When_Finding_Then_AvoidsIt()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(), spentAtHeight: 500);

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void Given_StaleChannel_When_StaleCheckRequested_Then_AvoidsIt()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(timestamp: 1_000_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18)
        {
            NowUnixSeconds = 1_000_000 + 1_209_601
        };

        // Act / Assert
        Assert.NotNull(_pathfinder.FindPath(kit.Build(), request));
        Assert.Null(_pathfinder.FindPath(kit.Build(), request with { StaleAfter = TimeSpan.FromDays(14) }));
    }

    [Fact]
    public void Given_Exclusions_When_Finding_Then_RespectsChannelEdgeAndNode()
    {
        // Arrange
        var kit = new GraphTestKit();
        var direct = kit.Channel("S", "T", GraphTestKit.Policy());
        kit.Channel("S", "X", GraphTestKit.Policy());
        kit.Channel("X", "T", GraphTestKit.Policy(1_000));
        var graph = kit.Build();
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18);

        // Act
        var byChannel = _pathfinder.FindPath(graph, request with
        {
            ExcludedChannels = new HashSet<ShortChannelId> { direct }
        });
        var byEdge = _pathfinder.FindPath(graph, request with
        {
            ExcludedEdges = new HashSet<DirectedChannel> { DirectedChannel.Between(direct, kit["S"], kit["T"]) }
        });
        var byReverseEdge = _pathfinder.FindPath(graph, request with
        {
            ExcludedEdges = new HashSet<DirectedChannel> { DirectedChannel.Between(direct, kit["T"], kit["S"]) }
        });
        var byNode = _pathfinder.FindPath(graph, request with
        {
            ExcludedChannels = new HashSet<ShortChannelId> { direct },
            ExcludedNodes = new HashSet<CompactPubKey> { kit["X"] }
        });

        // Assert
        Assert.Equal(2, byChannel!.Hops.Count);
        Assert.Equal(2, byEdge!.Hops.Count);
        Assert.Single(byReverseEdge!.Hops);
        Assert.Null(byNode);
    }

    [Fact]
    public void Given_PolicyOverride_When_Finding_Then_UsesTheOverriddenFee()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy());
        var xt = kit.Channel("X", "T", GraphTestKit.Policy(1_000));
        var directed = DirectedChannel.Between(xt, kit["X"], kit["T"]);
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18)
        {
            PolicyOverrides = new Dictionary<DirectedChannel, GraphPolicy>
            {
                [directed] = GraphTestKit.WithDirection(GraphTestKit.Policy(2_000, 10), directed.Direction)
            }
        };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.Equal(1_000_000UL + 2_000 + 10, path!.AmountMsat);
    }

    [Fact]
    public void Given_AmountAboveCapacity_When_Finding_Then_AvoidsTheChannel()
    {
        // Arrange: 1,000 sat channel
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(htlcMax: 1_000_000), capacitySat: 1_000);

        // Act / Assert
        Assert.NotNull(_pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18)));
        Assert.Null(_pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_001, 18)));
    }

    [Fact]
    public void Given_HtlcMaxAboveCapacity_When_Finding_Then_TheDirectionIsIgnored()
    {
        // Arrange: B7-CU-03, htlc_maximum_msat 1,000,001 on a 1,000 sat channel
        var kit = new GraphTestKit();
        kit.Channel("S", "T", GraphTestKit.Policy(htlcMax: 1_000_001), capacitySat: 1_000);

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void Given_LocalChannels_When_OneIsUnusableOrShort_Then_UsesTheOther()
    {
        // Arrange
        var kit = new GraphTestKit();
        var viaA = kit.Channel("S", "A", GraphTestKit.Policy());
        kit.Channel("A", "T", GraphTestKit.Policy());
        var viaB = kit.Channel("S", "B", GraphTestKit.Policy());
        kit.Channel("B", "T", GraphTestKit.Policy(10_000));
        var graph = kit.Build();
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18);

        // Act
        var aUnusable = _pathfinder.FindPath(graph, request with
        {
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState>
            {
                [viaA] = new(false, 10_000_000),
                [viaB] = new(true, 10_000_000)
            }
        });
        var aShort = _pathfinder.FindPath(graph, request with
        {
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState>
            {
                [viaA] = new(true, 999_999),
                [viaB] = new(true, 10_000_000)
            }
        });
        var aUnlisted = _pathfinder.FindPath(graph, request with
        {
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState> { [viaB] = new(true, 10_000_000) }
        });

        // Assert
        Assert.Equal(kit["B"], aUnusable!.Hops[0].NodeId);
        Assert.Equal(kit["B"], aShort!.Hops[0].NodeId);
        Assert.Equal(kit["B"], aUnlisted!.Hops[0].NodeId);
        Assert.Equal(1.0, aShort.Hops[0].Probability);
    }

    [Fact]
    public void Given_OurPolicyDisabled_When_LocalStateSaysUsable_Then_TheLiveStateWins()
    {
        // Arrange
        var kit = new GraphTestKit();
        var ours = kit.Channel("S", "T", GraphTestKit.Policy(disabled: true));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18);

        // Act
        var withoutLocal = _pathfinder.FindPath(kit.Build(), request);
        var withLocal = _pathfinder.FindPath(kit.Build(), request with
        {
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState> { [ours] = new(true, 5_000) }
        });

        // Assert
        Assert.Null(withoutLocal);
        Assert.NotNull(withLocal);
    }

    [Fact]
    public void Given_OurStaleChannel_When_LocalStateSaysUsable_Then_TheLiveStateWins()
    {
        // Arrange: both policies of our channel are older than two weeks
        var kit = new GraphTestKit();
        var ours = kit.Channel("S", "T", GraphTestKit.Policy(timestamp: 1_000_000),
                               GraphTestKit.Policy(timestamp: 1_000_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18)
        {
            NowUnixSeconds = 1_000_000 + 1_209_601,
            StaleAfter = TimeSpan.FromDays(14)
        };

        // Act
        var withoutLocal = _pathfinder.FindPath(kit.Build(), request);
        var withLocal = _pathfinder.FindPath(kit.Build(), request with
        {
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState> { [ours] = new(true, 10_000_000) }
        });

        // Assert
        Assert.Null(withoutLocal);
        Assert.NotNull(withLocal);
        Assert.Equal(1.0, withLocal.Hops[0].Probability);
    }

    [Fact]
    public void Given_StaleChannelBeyondOurFirstHop_When_LocalStateSaysUsable_Then_ItIsStillSkipped()
    {
        // Arrange: our channel is fresh, the next one is stale
        var kit = new GraphTestKit();
        const uint now = 1_000_000 + 1_209_601;
        var ours = kit.Channel("S", "X", GraphTestKit.Policy(timestamp: now), GraphTestKit.Policy(timestamp: now));
        kit.Channel("X", "T", GraphTestKit.Policy(timestamp: 1_000_000), GraphTestKit.Policy(timestamp: 1_000_000));
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18)
        {
            NowUnixSeconds = now,
            StaleAfter = TimeSpan.FromDays(14),
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState> { [ours] = new(true, 10_000_000) }
        };

        // Act / Assert
        Assert.Null(_pathfinder.FindPath(kit.Build(), request));
    }

    [Fact]
    public void Given_PrivatePayeeWithRouteHint_When_Finding_Then_CombinesGraphAndHintEdge()
    {
        // Arrange: S - X public, X -> P only in the invoice's route hint
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy());
        var payee = GraphTestKit.NodeId(999);
        var hintScid = new ShortChannelId(900, 7, 1);
        var request = new PathfindingRequest(kit["S"], payee, 2_000_000, 18)
        {
            ExtraEdges = [new ExtraEdge(kit["X"], payee, hintScid, GraphTestKit.Policy(500, 1_000, 30))]
        };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.NotNull(path);
        Assert.Equal([kit["X"], payee], path.Hops.Select(h => h.NodeId));
        Assert.Equal(hintScid, path.Hops[1].ShortChannelId);
        Assert.Equal(2_000_000UL + 500 + 2_000, path.AmountMsat);
        Assert.Equal(48u, path.TotalCltvDelta);
    }

    [Fact]
    public void Given_OurPrivateChannelAsExtraEdge_When_Finding_Then_UsesIt()
    {
        // Arrange: no graph at all
        var us = GraphTestKit.NodeId(1);
        var peer = GraphTestKit.NodeId(2);
        var scid = new ShortChannelId(800, 1, 0);
        var request = new PathfindingRequest(us, peer, 1_000, 18)
        {
            ExtraEdges = [new ExtraEdge(us, peer, scid, GraphTestKit.Policy())],
            LocalChannels = new Dictionary<ShortChannelId, LocalChannelState> { [scid] = new(true, 1_000) }
        };

        // Act
        var path = _pathfinder.FindPath(GraphSnapshot.Empty, request);

        // Assert
        Assert.Equal(scid, path!.FirstChannel);
    }

    [Fact]
    public void Given_TwoDisjointRoutes_When_FindingThreePaths_Then_ReturnsBothDistinctCheapestFirst()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "A", GraphTestKit.Policy());
        kit.Channel("A", "T", GraphTestKit.Policy(1_000));
        kit.Channel("S", "B", GraphTestKit.Policy());
        kit.Channel("B", "T", GraphTestKit.Policy(3_000));

        // Act
        var paths = _pathfinder.FindPaths(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18), 3);

        // Assert
        Assert.Equal(2, paths.Count);
        Assert.Equal(kit["A"], paths[0].Hops[0].NodeId);
        Assert.Equal(kit["B"], paths[1].Hops[0].NodeId);
    }

    [Fact]
    public void Given_SharedLastHop_When_FindingDiversePaths_Then_FirstHopsDiffer()
    {
        // Arrange: three first hops that all reach T through M
        var kit = new GraphTestKit();
        foreach (var name in new[] { "A", "B", "C" })
        {
            kit.Channel("S", name, GraphTestKit.Policy());
            kit.Channel(name, "M", GraphTestKit.Policy(name == "A" ? 100u : name == "B" ? 200u : 300u));
        }

        kit.Channel("M", "T", GraphTestKit.Policy(1_000));

        // Act
        var paths = _pathfinder.FindPaths(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18), 3);

        // Assert
        Assert.Equal(3, paths.Count);
        Assert.Equal([kit["A"], kit["B"], kit["C"]], paths.Select(p => p.Hops[0].NodeId));
        Assert.All(paths, p => Assert.Equal(kit["M"], p.Hops[1].NodeId));
    }

    [Fact]
    public void Given_LiquidityFailureOnCheapRoute_When_Finding_Then_PrefersTheOtherRoute()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "A", GraphTestKit.Policy());
        var at = kit.Channel("A", "T", GraphTestKit.Policy(1_000));
        kit.Channel("S", "B", GraphTestKit.Policy());
        kit.Channel("B", "T", GraphTestKit.Policy(3_000));
        var liquidity = new LiquidityEstimates();
        liquidity.RecordFailure(DirectedChannel.Between(at, kit["A"], kit["T"]), 500_000, 1_000);
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000_000, 18)
        {
            Liquidity = liquidity,
            NowUnixSeconds = 1_000
        };

        // Act
        var path = _pathfinder.FindPath(kit.Build(), request);

        // Assert
        Assert.Equal(kit["B"], path!.Hops[0].NodeId);
    }

    [Fact]
    public void Given_SameRequest_When_RunTwice_Then_SamePath()
    {
        // Arrange: many equal-cost routes
        var kit = new GraphTestKit();
        for (var i = 0; i < 10; i++)
        {
            kit.Channel("S", $"X{i}", GraphTestKit.Policy());
            kit.Channel($"X{i}", "T", GraphTestKit.Policy(100));
        }

        var graph = kit.Build();
        var request = new PathfindingRequest(kit["S"], kit["T"], 1_000, 18);

        // Act
        var first = _pathfinder.FindPaths(graph, request, 3);
        var second = _pathfinder.FindPaths(graph, request, 3);

        // Assert
        Assert.Equal(first.Select(p => p.FirstChannel), second.Select(p => p.FirstChannel));
    }

    [Fact]
    public void Given_UnknownTarget_When_Finding_Then_NoPath()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "X", GraphTestKit.Policy());

        // Act
        var path = _pathfinder.FindPath(kit.Build(), new PathfindingRequest(kit["S"], GraphTestKit.NodeId(777), 1, 18));

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void Given_SourceIsTarget_When_Finding_Then_Throws()
    {
        // Arrange
        var node = GraphTestKit.NodeId(1);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => _pathfinder.FindPath(GraphSnapshot.Empty,
                                                                      new PathfindingRequest(node, node, 1, 18)));
    }

    [Fact]
    public void Given_FiftyThousandChannelGraph_When_Finding_Then_ItIsFast()
    {
        // Arrange: 10,000 nodes, 50,000 random channels (fixed seed)
        var random = new Random(42);
        var nodes = Enumerable.Range(1, 10_000).Select(GraphTestKit.NodeId).ToArray();
        var channels = new List<GraphChannel>();
        var seen = new HashSet<(int, int)>();
        uint block = 1;
        while (channels.Count < 50_000)
        {
            var i = random.Next(nodes.Length);
            var j = random.Next(nodes.Length);
            if (i == j || !seen.Add((Math.Min(i, j), Math.Max(i, j))))
                continue;

            var (n1, n2) = i < j ? (nodes[i], nodes[j]) : (nodes[j], nodes[i]);
            var channel = new GraphChannel(new ShortChannelId(block++, 0, 0), n1, n2, n1, n2, 10_000_000)
                         .WithPolicy(RandomPolicy(random, 0))
                         .WithPolicy(RandomPolicy(random, 1));
            channels.Add(channel);
        }

        var graph = new GraphSnapshot(channels, []);
        var pathfinder = new GraphPathfinder();
        var requests = new List<PathfindingRequest>();
        while (requests.Count < 20)
        {
            var source = nodes[random.Next(nodes.Length)];
            var target = nodes[random.Next(nodes.Length)];
            if (source != target)
                requests.Add(new PathfindingRequest(source, target, 100_000_000, 18));
        }

        pathfinder.FindPath(graph, requests[0]); // warm up

        // Act
        var stopwatch = Stopwatch.StartNew();
        var found = requests.Count(r => pathfinder.FindPath(graph, r) is not null);
        stopwatch.Stop();

        // Assert: the plan's budget (G4-T1) is 50 ms per query in Release; a Debug build gets 5x
        var perQuery = stopwatch.Elapsed.TotalMilliseconds / requests.Count;
        TestContext.Current.TestOutputHelper?.WriteLine($"{perQuery:F1} ms per query, {found}/{requests.Count} found");
        Assert.True(found > 0);
#if DEBUG
        const double budgetMs = 250;
#else
        const double budgetMs = 50;
#endif
        Assert.True(perQuery < budgetMs, $"{perQuery:F1} ms per query, budget {budgetMs} ms");
    }

    private static GraphPolicy RandomPolicy(Random random, byte direction) =>
        new(GraphTestKit.Timestamp, 1, direction, (ushort)random.Next(18, 145), 1_000, 5_000_000_000,
            (uint)random.Next(0, 2_000), (uint)random.Next(0, 1_000));

    private static int IndexOf(IGraphView graph, CompactPubKey nodeId)
    {
        Assert.True(graph.TryGetNodeIndex(nodeId, out var index));
        return index;
    }

    private static byte[] FeatureBits(int bit)
    {
        var bytes = new byte[bit / 8 + 1];
        bytes[^(bit / 8 + 1)] = (byte)(1 << (bit % 8));
        return bytes;
    }
}