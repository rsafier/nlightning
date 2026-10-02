namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Topology;

public class ClusterTopologyFixtureTests
{
    private sealed class Pair : IClusterTopologyDefinition
    {
        public static string Suite => "unit-pair";

        public static void Configure(TopologyBuilder builder) =>
            builder.AddBitcoinCore("miner").AddCln("alice").AddCln("bob");
    }

    [Fact]
    public void Given_AFixtureNotStartedByXunit_When_ItsTopologyIsRead_Then_ItSaysHowToUseIt()
    {
        // Arrange
        var fixture = new ClusterTopologyFixture<Pair>();

        // Act
        var topology = Assert.Throws<InvalidOperationException>(() => fixture.Topology);
        var run = Assert.Throws<InvalidOperationException>(() => fixture.Run);

        // Assert
        Assert.Contains("unit-pair", topology.Message);
        Assert.Contains("collection", run.Message);
        Assert.Empty(fixture.StartLog);
    }

    [Fact]
    public async Task Given_AFixtureNeverStarted_When_Disposed_Then_ItDoesNothing()
    {
        // Arrange
        var fixture = new ClusterTopologyFixture<Pair>();

        // Act + Assert (no run to delete, twice is fine)
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
    }
}