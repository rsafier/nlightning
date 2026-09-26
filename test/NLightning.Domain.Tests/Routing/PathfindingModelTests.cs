namespace NLightning.Domain.Tests.Routing;

using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Routing.Pathfinding;

public class PathfindingModelTests
{
    [Fact]
    public void Given_EdgeEnds_When_Directing_Then_LesserNodeSendsDirectionZero()
    {
        // Arrange
        var low = GraphTestKit.NodeId(1);
        var high = GraphTestKit.NodeId(2);
        var scid = new ShortChannelId(5, 0, 0);

        // Act / Assert
        Assert.Equal(0, DirectedChannel.Between(scid, low, high).Direction);
        Assert.Equal(1, DirectedChannel.Between(scid, high, low).Direction);
    }

    [Fact]
    public void Given_SeededRandom_When_ComputingShadowOffset_Then_DeterministicAndCapped()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("T", "A", GraphTestKit.Policy(cltvDelta: 100), GraphTestKit.Policy(cltvDelta: 100));
        kit.Channel("A", "B", GraphTestKit.Policy(cltvDelta: 100), GraphTestKit.Policy(cltvDelta: 100));
        var graph = kit.Build();

        // Act
        var first = ShadowCltv.ComputeOffset(graph, kit["T"], new Random(7));
        var second = ShadowCltv.ComputeOffset(graph, kit["T"], new Random(7));
        var capped = Enumerable.Range(0, 50).Select(s => ShadowCltv.ComputeOffset(graph, kit["T"], new Random(s)))
                               .ToList();

        // Assert
        Assert.Equal(first, second);
        Assert.All(capped, o => Assert.InRange(o, 100u, ShadowCltv.DefaultMaxOffset));
    }

    [Fact]
    public void Given_PayeeNotInGraph_When_ComputingShadowOffset_Then_WithinTheCap()
    {
        // Act
        var offset = ShadowCltv.ComputeOffset(GraphSnapshot.Empty, GraphTestKit.NodeId(5), new Random(1), 20);

        // Assert
        Assert.InRange(offset, 0u, 20u);
    }

    [Fact]
    public void Given_CostModel_When_ProbabilityDrops_Then_CostRises()
    {
        // Arrange
        var model = PathCostModel.Default;

        // Act
        var certain = model.EdgeCost(100, 1_000_000, 40, 1.0);
        var likely = model.EdgeCost(100, 1_000_000, 40, 0.6);

        // Assert: fee + 1e6 * 40 * 15 / 1e6 = 100 + 600
        Assert.Equal(700, certain, 6);
        Assert.True(likely > certain);
        Assert.Equal(0, model.DiversityPenalty(0, 1_000_000));
        Assert.Equal(20_000, model.DiversityPenalty(2, 1_000));
    }

    [Fact]
    public void Given_UnverifiedChannel_When_Finding_Then_VerifiedRouteIsPreferred()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Channel("S", "A", GraphTestKit.Policy());
        kit.Channel("A", "T", GraphTestKit.Policy(), verification: GraphChannelVerification.Unverified);
        kit.Channel("S", "B", GraphTestKit.Policy());
        kit.Channel("B", "T", GraphTestKit.Policy());

        // Act
        var path = new GraphPathfinder().FindPath(kit.Build(), new PathfindingRequest(kit["S"], kit["T"], 1_000, 18));

        // Assert
        Assert.Equal(kit["B"], path!.Hops[0].NodeId);
        Assert.Equal(PathCostModel.Default.AprioriProbability * PathCostModel.Default.AprioriProbability,
                     path.Probability, 10);
    }
}