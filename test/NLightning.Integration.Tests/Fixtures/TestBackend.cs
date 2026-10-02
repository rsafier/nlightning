namespace NLightning.Integration.Tests.Fixtures;

/// <summary>Where a ported fixture runs its peers and its bitcoind.</summary>
public enum TestBackendKind
{
    /// <summary>Docker containers on this machine (the default, today's suites).</summary>
    Docker,

    /// <summary>The Kubernetes harness (<c>test/NLightning.Testing.Cluster</c>), one namespace per run.</summary>
    Cluster
}

/// <summary>
/// The backend switch of the ported suites (test harness phase 2): <c>NLTG_TEST_BACKEND=docker|cluster</c>, Docker
/// when unset. A fixture that has both backends (<see cref="ClnFixture"/>) reads it once when xunit creates it; a
/// fixture that has only Docker (<see cref="TorInteropFixture"/>) refuses the cluster backend with a skip.
/// </summary>
public static class TestBackend
{
    public const string EnvironmentVariable = "NLTG_TEST_BACKEND";

    /// <summary>The backend of this process (<see cref="EnvironmentVariable"/>).</summary>
    public static TestBackendKind Current => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool IsCluster => Current == TestBackendKind.Cluster;

    /// <summary>
    /// <c>docker</c> (or empty) and <c>cluster</c>, case-insensitive.
    /// </summary>
    /// <exception cref="ArgumentException">Any other value: a typo must not fall back to Docker silently.</exception>
    public static TestBackendKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "docker" => TestBackendKind.Docker,
            "cluster" or "k8s" or "kubernetes" => TestBackendKind.Cluster,
            _ => throw new ArgumentException($"{EnvironmentVariable}={value}: expected docker or cluster", nameof(value))
        };

    /// <summary>
    /// Skips the current test on the cluster backend, with <paramref name="reason"/> (for tests that run on Docker
    /// only).
    /// </summary>
    public static void SkipOnCluster(string reason)
    {
        if (IsCluster)
            Assert.Skip($"Not on the cluster backend ({EnvironmentVariable}=cluster): {reason}");
    }
}