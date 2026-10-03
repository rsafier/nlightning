namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// The LND regtest network runs on the cluster backend only (NL-820): elsewhere the fixture starts nothing and every
/// test that uses it is skipped with the reason, never passed or failed.
/// </summary>
public class LightningRegtestNetworkFixtureTests
{
    private static readonly Action s_kubeConfigured = () => { };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("docker")]
    public async Task Given_TheBackendIsNotTheCluster_When_TheFixtureStarts_Then_ItStartsNothingAndSaysWhy(
        string? backend)
    {
        // Arrange
        var probed = false;
        await using var fixture = new LightningRegtestNetworkFixture(Environment(backend), () => probed = true);

        // Act
        await fixture.InitializeAsync();

        // Assert: no Kubernetes configuration read, the reason names the cluster runner
        Assert.False(probed);
        Assert.NotNull(fixture.UnavailableReason);
        Assert.Contains("cluster backend only", fixture.UnavailableReason);
        Assert.Contains($"{TestBackend.EnvironmentVariable}=cluster", fixture.UnavailableReason);
        Assert.Contains("scripts/run-cluster.sh", fixture.UnavailableReason);
    }

    [Fact]
    public async Task Given_TheClusterBackendWithoutAKubeConfiguration_When_TheFixtureStarts_Then_ItSaysNoClusterIsConfigured()
    {
        // Arrange
        await using var fixture = new LightningRegtestNetworkFixture(
            Environment("cluster"), () => throw new FileNotFoundException("no kubeconfig at ~/.kube/config"));

        // Act
        await fixture.InitializeAsync();

        // Assert
        Assert.NotNull(fixture.UnavailableReason);
        Assert.Contains("no Kubernetes cluster is configured", fixture.UnavailableReason);
        Assert.Contains("no kubeconfig at ~/.kube/config", fixture.UnavailableReason);
    }

    [Fact]
    public async Task Given_TheClusterBackendWithAKubeConfiguration_When_Built_Then_TheNetworkIsAvailable()
    {
        // Arrange & Act: building the fixture makes no cluster call (that is InitializeAsync's)
        await using var fixture = new LightningRegtestNetworkFixture(Environment("cluster"), s_kubeConfigured);

        // Assert
        Assert.Null(fixture.UnavailableReason);
        fixture.SkipIfUnavailable();
        Assert.Throws<InvalidOperationException>(() => fixture.Bitcoin);
    }

    [Fact]
    public async Task Given_AnUnavailableNetwork_When_ATestUsesIt_Then_EveryMemberSkipsTheTest()
    {
        // Arrange: a recording skip (Assert.Skip would skip this test itself)
        var skips = new List<string>();
        await using var fixture = new LightningRegtestNetworkFixture(Environment(null), s_kubeConfigured, reason =>
        {
            skips.Add(reason);
            throw new TestSkipped(reason);
        });
        await fixture.InitializeAsync();
        var factoryCalled = false;

        // Act & Assert: each member skips with the reason before it touches anything
        AssertSkips(() => fixture.SkipIfUnavailable());
        AssertSkips(() => _ = fixture.Bitcoin);
        AssertSkips(() => _ = fixture.BitcoinZmqPorts);
        AssertSkips(() => _ = fixture.LndNodes);
        AssertSkips(() => _ = fixture.Cluster);
        AssertSkips(() => _ = fixture.HostAddressForLnd);
        AssertSkips(() => fixture.GetLndNode("alice"));
        AssertSkips(() => fixture.RestartLndAsync("alice"));
        AssertSkips(() => fixture.GetOrCreateAsync("topology", () =>
        {
            factoryCalled = true;
            return Task.FromResult(new object());
        }));
        Assert.False(factoryCalled);
        Assert.Equal(9, skips.Count);
        Assert.All(skips, reason => Assert.Equal(fixture.UnavailableReason, reason));

        // A failed test's log dump stays harmless
        await fixture.DumpLndLogsAsync(["alice"]);

        void AssertSkips(Action use)
        {
            var skip = Assert.Throws<TestSkipped>(use);
            Assert.Equal(fixture.UnavailableReason, skip.Message);
        }
    }

    [Fact]
    public void Given_AMistypedBackend_When_TheFixtureIsBuilt_Then_ItThrowsInsteadOfSkipping()
    {
        // Act & Assert: a typo must not turn a whole suite into skips
        Assert.Throws<ArgumentException>(() => new LightningRegtestNetworkFixture(Environment("clustr"),
                                                                                  s_kubeConfigured));
    }

    private static Func<string, string?> Environment(string? backend) =>
        name => name == TestBackend.EnvironmentVariable ? backend : null;

    private sealed class TestSkipped(string reason) : Exception(reason);
}