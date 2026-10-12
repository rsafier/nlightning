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
    public async Task Given_AFixture_When_XunitInitializesIt_Then_NothingIsBuiltUntilATestAsks()
    {
        // Arrange: xunit creates the fixtures of Explicit tests that will not run too (NL-800)
        var fixture = new ClusterTopologyFixture<Pair>();

        // Act: no cluster here; an eager build would throw or start a namespace
        await fixture.InitializeAsync();

        // Assert
        Assert.Throws<InvalidOperationException>(() => fixture.Topology);
        Assert.Empty(fixture.StartLog);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task Given_ADisposedFixtureNeverStarted_When_ATestAsksForIt_Then_ItRefuses()
    {
        // Arrange
        var fixture = new ClusterTopologyFixture<Pair>();
        await fixture.DisposeAsync();

        // Act + Assert (refused before any task starts)
        Assert.Throws<ObjectDisposedException>(void () => _ = fixture.EnsureStartedAsync(CancellationToken.None));
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

    [Fact]
    public async Task Given_AFixtureWithAStoppingHook_When_Disposed_Then_TheHookRunsOnce()
    {
        // Arrange
        var fixture = new StoppingFixture();

        // Act
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal(1, fixture.Stops);
    }

    private sealed class StoppingFixture : ClusterTopologyFixture<Pair>
    {
        public int Stops { get; private set; }

        protected override ValueTask OnStoppingAsync()
        {
            Stops++;
            return ValueTask.CompletedTask;
        }
    }
}