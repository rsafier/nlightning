namespace NLightning.Integration.Tests.Fixtures;

using Testing.Cluster.Run;

/// <summary>
/// The opt-in of the cluster-backed fixtures: <c>NLTG_TEST_BACKEND=cluster</c>. The Kubernetes harness
/// (<c>test/NLightning.Testing.Cluster</c>) is the only backend of the LND regtest network (NL-820) and of the CLN,
/// Eclair, LDK and Postgres fixtures (NL-866): without the opt-in they start nothing and their tests are skipped with the
/// reason (<see cref="ClusterAvailability"/>); with it, a missing Kubernetes configuration fails them (NL-860).
/// <c>scripts/run-cluster.sh</c> sets it for every suite it runs. The Tor interop suite, the one suite left on Docker,
/// runs when it is unset and skips under it (<see cref="SkipOnCluster"/>).
/// </summary>
public static class TestBackend
{
    public const string EnvironmentVariable = "NLTG_TEST_BACKEND";

    /// <summary>Whether this process opted into the cluster (<see cref="EnvironmentVariable"/>).</summary>
    public static bool IsCluster => IsClusterValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// <c>cluster</c> (also <c>k8s</c> and <c>kubernetes</c>, case-insensitive) is true; unset or empty is false.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any other value, <c>docker</c> included (the Docker backends are retired, NL-866): a typo or a stale value must
    /// not turn a suite into skips silently.
    /// </exception>
    public static bool IsClusterValue(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => false,
            "cluster" or "k8s" or "kubernetes" => true,
            "docker" => throw new ArgumentException(
                            $"{EnvironmentVariable}=docker: the Docker backends are retired (NL-820, NL-866); leave it "
                          + "unset (the Tor suite runs on Docker without it) or set it to cluster", nameof(value)),
            _ => throw new ArgumentException($"{EnvironmentVariable}={value}: expected cluster or nothing",
                                             nameof(value))
        };

    /// <summary>
    /// Skips the current test under the cluster opt-in, with <paramref name="reason"/> (the Tor suite, Docker only).
    /// </summary>
    public static void SkipOnCluster(string reason)
    {
        if (IsCluster)
            Assert.Skip($"Not on the cluster backend ({EnvironmentVariable}=cluster): {reason}");
    }
}

/// <summary>
/// Whether a cluster-backed fixture can run in this process (NL-820, NL-860, NL-866): <see cref="UnavailableReason"/>
/// without the opt-in (every test that uses the fixture is skipped with it), <see cref="ConfigurationError"/> under the
/// opt-in when no Kubernetes configuration can be built (the fixture fails, never an all-skipped green), neither when
/// the fixture starts its cluster backend.
/// </summary>
public sealed class ClusterAvailability
{
    private readonly Action<string> _skip;

    /// <param name="what">What the fixture runs, for the messages ("the CLN fixture").</param>
    /// <param name="issue">The ledger entry that made the cluster its only backend (NL-820, NL-866).</param>
    /// <param name="runCommand">The command that runs its suites (<c>scripts/run-cluster.sh --matrix cln</c>).</param>
    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">
    /// Throws when no Kubernetes configuration can be built (no kubeconfig, or in a pod without its service account
    /// token); called only under the opt-in.
    /// </param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    /// <exception cref="ArgumentException">A mistyped or retired backend name (<see cref="TestBackend.IsClusterValue"/>).</exception>
    public ClusterAvailability(string what, string issue, string runCommand, Func<string, string?> environment,
                               Action kubeConfiguration, Action<string>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(kubeConfiguration);
        _skip = skip ?? (reason => Assert.Skip(reason));
        if (!TestBackend.IsClusterValue(environment(TestBackend.EnvironmentVariable)))
        {
            UnavailableReason = $"{Capitalize(what)} runs on the cluster backend only ({issue}): set "
                              + $"{TestBackend.EnvironmentVariable}=cluster or run {runCommand}";
            return;
        }

        try
        {
            kubeConfiguration();
        }
        catch (Exception e)
        {
            ConfigurationError = $"{TestBackend.EnvironmentVariable}=cluster, but no Kubernetes cluster is configured "
                               + $"to run {what} on ({e.GetType().Name}: {e.Message})";
        }
    }

    /// <summary>
    /// Why the fixture does not run in this process (the skip reason of its tests): no cluster opt-in. Null under it.
    /// </summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// Under the opt-in, why no Kubernetes configuration could be built; the fixture then fails as a fixture failure
    /// instead of skipping (NL-860). Null otherwise.
    /// </summary>
    public string? ConfigurationError { get; }

    /// <summary>Whether the fixture may start its cluster backend.</summary>
    public bool CanStart => UnavailableReason is null && ConfigurationError is null;

    /// <summary>The availability of this process (its environment and the default Kubernetes configuration).</summary>
    public static ClusterAvailability FromEnvironment(string what, string issue, string runCommand) =>
        new(what, issue, runCommand, Environment.GetEnvironmentVariable, KubeConfigurationProbe);

    /// <summary>Builds the default Kubernetes configuration (kubeconfig or in-cluster); throws when there is none.</summary>
    public static void KubeConfigurationProbe() => KubeClientFactory.BuildConfiguration();

    /// <summary>
    /// Skips the current test when the fixture does not run in this process (<see cref="UnavailableReason"/>).
    /// </summary>
    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
            _skip(UnavailableReason);
    }

    /// <summary>Throws <see cref="ConfigurationError"/> as an <see cref="InvalidOperationException"/>, if any.</summary>
    public void ThrowIfMisconfigured()
    {
        if (ConfigurationError is not null)
            throw new InvalidOperationException(ConfigurationError);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}