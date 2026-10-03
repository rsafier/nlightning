namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// Whether the LND suites may run on the cluster backend: <c>LightningRegtestNetworkFixture</c> must construct its
/// cluster backend, <c>ClusterLndBackend</c> (test harness phase 3 wiring). A fixture that only delegates to
/// <c>ILndNetworkBackend</c> may still always build the Docker backend (as lane <c>hf-lnunit</c>'s did), and would then
/// ignore <c>NLTG_TEST_BACKEND=cluster</c> and start Docker containers, which a cluster run must never do (one Docker
/// test process at a time machine-wide), so the runner skips those suites and says why. What the individual tests do
/// on the cluster is the catalog's (<see cref="MatrixSuite.ClusterProofPending"/>) and the tests' own business: test
/// code that drives Docker containers by name skips itself there (<c>LightningRegtestNetworkFixture.SkipUnlessDocker</c>).
/// </summary>
public static class LndBackendProbe
{
    /// <summary>The fixture's source, relative to the repository root.</summary>
    public const string FixturePath = "test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs";

    /// <summary>The cluster backend the wired fixture constructs.</summary>
    public const string Marker = "new ClusterLndBackend(";

    /// <summary>Whether <paramref name="fixtureSource"/> constructs the cluster backend.</summary>
    public static bool IsWired(string? fixtureSource) =>
        fixtureSource is not null && fixtureSource.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Whether the fixture under <paramref name="repoRoot"/> is wired (false when the file is missing).</summary>
    public static bool IsWiredIn(string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        var path = Path.Combine(repoRoot, FixturePath);
        return File.Exists(path) && IsWired(File.ReadAllText(path));
    }
}