namespace NLightning.Testing.Cluster.Tests.Nodes.Lnd;

using Cluster.Nodes.Lnd;
using Testing.Lnd.Lnrpc;

public class LndGraphTests
{
    private const string Funder = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Peer = "03bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static RoutingPolicy Policy(long baseFee, long rate, uint delta, bool disabled = false) =>
        new() { FeeBaseMsat = baseFee, FeeRateMilliMsat = rate, TimeLockDelta = delta, Disabled = disabled };

    private static ChannelEdge Edge(RoutingPolicy? funder, RoutingPolicy? peer, bool funderIsNode1 = true)
    {
        var edge = new ChannelEdge
        {
            ChannelId = 42,
            Node1Pub = funderIsNode1 ? Funder : Peer,
            Node2Pub = funderIsNode1 ? Peer : Funder
        };
        if ((funderIsNode1 ? funder : peer) is { } first)
            edge.Node1Policy = first;
        if ((funderIsNode1 ? peer : funder) is { } second)
            edge.Node2Policy = second;
        return edge;
    }

    [Fact]
    public void Given_AnEdgeWithBothPoliciesAndTheFundersPolicy_When_Checked_Then_ItIsRoutable()
    {
        // Arrange: LNUnit's funder policy (0 msat, 0 ppm, delta 40), LND's defaults on the peer's side
        var edge = Edge(Policy(0, 0, 40), Policy(1000, 1, 80), funderIsNode1: false);

        // Act & Assert
        Assert.Null(LndGraph.EdgeProblem(edge, Funder, 0, 0, 40));
        Assert.Null(LndGraph.EdgeProblem(edge, Funder));
        Assert.Same(edge.Node2Policy, LndGraph.OwnPolicy(edge, Funder));
        Assert.Same(edge.Node1Policy, LndGraph.OwnPolicy(edge, Peer.ToUpperInvariant()));
        Assert.Null(LndGraph.OwnPolicy(edge, "02cc"));
    }

    [Fact]
    public void Given_AnEdgeNotYetComplete_When_Checked_Then_ItSaysWhatIsMissing()
    {
        // Act & Assert
        Assert.Equal("not in the graph", LndGraph.EdgeProblem(null, Funder));
        Assert.StartsWith("no policy of 03bbbbbb", LndGraph.EdgeProblem(Edge(Policy(0, 0, 40), null), Funder));
        Assert.StartsWith("no policy of 02aaaaaa", LndGraph.EdgeProblem(Edge(null, Policy(0, 0, 40)), Funder));
        Assert.Contains("disabled",
                        LndGraph.EdgeProblem(Edge(Policy(0, 0, 40, disabled: true), Policy(1000, 1, 80)), Funder));
        Assert.Contains("not an end", LndGraph.EdgeProblem(Edge(Policy(0, 0, 40), Policy(1000, 1, 80)), "02cc"));
    }

    [Fact]
    public void Given_TheFundersOldPolicy_When_CheckedForTheNewOne_Then_ItIsNotReadyYet()
    {
        // Arrange: the funder's update has not reached this node's graph yet (LND's defaults still there)
        var edge = Edge(Policy(1000, 1, 80), Policy(1000, 1, 80));

        // Act
        var problem = LndGraph.EdgeProblem(edge, Funder, 0, 0, 40);

        // Assert
        Assert.NotNull(problem);
        Assert.Contains("1000 msat + 1 ppm, delta 80", problem);
        Assert.Null(LndGraph.EdgeProblem(edge, Funder, 1000, 1, 80));
    }
}