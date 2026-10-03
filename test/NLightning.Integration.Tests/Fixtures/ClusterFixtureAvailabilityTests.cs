namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// The CLN, Eclair, LDK and Cashu mint fixtures run on the cluster only (NL-866, NL-993): without <c>NLTG_TEST_BACKEND=cluster</c> they start
/// nothing and every member a test uses skips it with the reason; under it without a Kubernetes configuration they fail
/// (NL-860). The LND network's and Postgres' cases are <see cref="LightningRegtestNetworkFixtureTests"/> and
/// <c>Postgres/PostgresBackendTests</c>.
/// </summary>
public class ClusterFixtureAvailabilityTests
{
    private static readonly Action s_kubeNotProbed = () => Assert.Fail("Kubernetes configuration read");

    [Fact]
    public async Task Given_NoClusterOptIn_When_TheClnFixtureStarts_Then_ItStartsNothingAndEveryMemberSkips()
    {
        // Arrange
        var skips = new List<string>();
        await using var fixture = new ClnFixture(_ => null, s_kubeNotProbed, RecordingSkip(skips));

        // Act
        await fixture.InitializeAsync();

        // Assert
        Assert.Contains("The CLN fixture runs on the cluster backend only (NL-866)", fixture.UnavailableReason);
        Assert.Contains("scripts/run-cluster.sh --matrix cln", fixture.UnavailableReason);
        AssertSkips(fixture.SkipIfUnavailable);
        AssertSkips(() => _ = fixture.Bitcoin);
        AssertSkips(() => _ = fixture.Cln);
        AssertSkips(() => _ = fixture.ClnAddress);
        AssertSkips(() => _ = fixture.HostAddressForCln);
        AssertSkips(() => fixture.GetOrCreateAsync("session", () => Task.FromResult(new object())));
        AssertSkips(() => fixture.StartClnAsync(new Cln.ClnNodeSpec("nltg-cln2"), CancellationToken.None));
        Assert.Equal(7, skips.Count);
        Assert.All(skips, reason => Assert.Equal(fixture.UnavailableReason, reason));

        // A failed test's log dump stays harmless
        await fixture.DumpClnLogAsync();
    }

    [Fact]
    public async Task Given_NoClusterOptIn_When_TheEclairFixtureStarts_Then_ItStartsNothingAndEveryMemberSkips()
    {
        // Arrange
        var skips = new List<string>();
        await using var fixture = new EclairFixture(_ => null, s_kubeNotProbed, RecordingSkip(skips));

        // Act
        await fixture.InitializeAsync();

        // Assert
        Assert.Contains("scripts/run-cluster.sh --matrix eclair,eclair2", fixture.UnavailableReason);
        AssertSkips(() => _ = fixture.Bitcoin);
        AssertSkips(() => _ = fixture.Eclair);
        AssertSkips(() => _ = fixture.EclairAddress);
        AssertSkips(() => fixture.RestartEclairAsync(CancellationToken.None));
        AssertSkips(() => fixture.GetSellerAsync(CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(5, skips.Count);
        await fixture.DumpEclairLogAsync();
        await fixture.DumpSellerLogAsync();
    }

    [Fact]
    public async Task Given_NoClusterOptIn_When_TheLdkFixtureStarts_Then_ItStartsNothingAndEveryMemberSkips()
    {
        // Arrange
        var skips = new List<string>();
        await using var fixture = new LdkFixture(_ => null, s_kubeNotProbed, RecordingSkip(skips));

        // Act
        await fixture.InitializeAsync();

        // Assert
        Assert.Contains("scripts/run-cluster.sh --matrix ldk", fixture.UnavailableReason);
        AssertSkips(() => _ = fixture.Bitcoin);
        AssertSkips(() => _ = fixture.Ldk);
        AssertSkips(() => _ = fixture.LdkAddress);
        AssertSkips(() => fixture.RestartLdkAsync(CancellationToken.None));
        Assert.Equal(4, skips.Count);
        await fixture.DumpLdkLogAsync();
    }

    [Fact]
    public async Task Given_NoClusterOptIn_When_TheCashuMintFixtureStarts_Then_ItStartsNothingAndItsTestsSkip()
    {
        // Arrange
        var skips = new List<string>();
        await using var fixture = new Cashu.CashuMintFixture(_ => null, s_kubeNotProbed, RecordingSkip(skips));

        // Act
        await fixture.InitializeAsync();

        // Assert
        Assert.Contains("The Cashu mint fixture runs on the cluster backend only (NL-993)", fixture.UnavailableReason);
        Assert.Contains("scripts/run-cluster.sh --matrix cashu", fixture.UnavailableReason);
        AssertSkips(fixture.SkipIfUnavailable);
        Assert.Equal([fixture.UnavailableReason!], skips);
        Assert.Equal("(no mint)", await fixture.GetMintLogAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Given_TheClusterOptInWithoutAKubeConfiguration_When_TheFixturesStart_Then_TheyFailInsteadOfSkipping()
    {
        // Arrange (NL-860)
        static string? Cluster(string name) => name == TestBackend.EnvironmentVariable ? "cluster" : null;
        Action noKube = () => throw new FileNotFoundException("no kubeconfig");
        Action<string> noSkip = reason => Assert.Fail($"skipped: {reason}");
        await using var cln = new ClnFixture(Cluster, noKube, noSkip);
        await using var eclair = new EclairFixture(Cluster, noKube, noSkip);
        await using var ldk = new LdkFixture(Cluster, noKube, noSkip);
        await using var cashu = new Cashu.CashuMintFixture(Cluster, noKube, noSkip);

        // Act & Assert
        foreach (var start in new Func<ValueTask>[]
                 {
                     cln.InitializeAsync, eclair.InitializeAsync, ldk.InitializeAsync, cashu.InitializeAsync
                 })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await start());
            Assert.Contains("no Kubernetes cluster is configured", error.Message);
        }

        Assert.Null(cln.UnavailableReason);
        Assert.NotNull(cln.ConfigurationError);
    }

    private static Action<string> RecordingSkip(List<string> skips) => reason =>
    {
        skips.Add(reason);
        throw new TestSkipped(reason);
    };

    private static void AssertSkips(Action use) => Assert.Throws<TestSkipped>(use);

    private sealed class TestSkipped(string reason) : Exception(reason);
}