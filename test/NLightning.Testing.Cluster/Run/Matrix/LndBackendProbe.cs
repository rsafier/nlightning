namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// Whether the LND suites may run on the cluster backend: <c>LightningRegtestNetworkFixture</c> must delegate to
/// <c>ILndNetworkBackend</c> (test harness phase 3 wiring). Until then the fixture ignores
/// <c>NLTG_TEST_BACKEND=cluster</c> and starts Docker containers, which a cluster run must never do (one Docker test
/// process at a time machine-wide), so the runner skips those suites and says why.
/// </summary>
public static class LndBackendProbe
{
    /// <summary>The fixture's source, relative to the repository root.</summary>
    public const string FixturePath = "test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs";

    /// <summary>The adapter the wired fixture delegates to.</summary>
    public const string Marker = "ILndNetworkBackend";

    /// <summary>Whether <paramref name="fixtureSource"/> delegates to the backend adapter.</summary>
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