namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// How a <see cref="TestRun"/> is named and where it runs.
/// </summary>
public sealed record TestRunOptions
{
    /// <summary>The namespace prefix of the spike (<c>nltg-spike-&lt;run&gt;</c>).</summary>
    public const string SpikeNamespacePrefix = "nltg-spike";

    /// <summary>The namespace prefix of the real harness (<c>nltg-&lt;run&gt;</c>).</summary>
    public const string DefaultNamespacePrefix = "nltg";

    /// <summary>Overrides <see cref="NamespacePrefix"/> when set.</summary>
    public const string NamespacePrefixVariable = "NLTG_TEST_NAMESPACE_PREFIX";

    /// <summary>Set to <c>1</c> or <c>true</c> to keep the namespace after the run, for debugging.</summary>
    public const string KeepNamespaceVariable = "NLTG_KEEP_NAMESPACE";

    /// <summary>
    /// Set to <c>1</c> or <c>true</c> to adopt the run's existing namespace instead of creating one (the in-cluster
    /// runner, whose ServiceAccount cannot create namespaces; see <see cref="Runner.TestRunnerJob"/>).
    /// </summary>
    public const string AdoptNamespaceVariable = "NLTG_ADOPT_NAMESPACE";

    /// <summary>The longest namespace prefix (the run id gets the rest of the 63 characters).</summary>
    public const int MaxPrefixLength = 22;

    /// <summary>The run id; null takes <see cref="TestRunId.EnvironmentVariable"/> or generates one.</summary>
    public string? RunId { get; init; }

    /// <summary>The suite (test collection) name, for the <see cref="RunLabels.Suite"/> label.</summary>
    public string Suite { get; init; } = "default";

    /// <summary>The namespace is <c>&lt;prefix&gt;-&lt;run id&gt;</c>.</summary>
    public string NamespacePrefix { get; init; } = SpikeNamespacePrefix;

    /// <summary>Whether the objects carry <c>nltg.spike=true</c>.</summary>
    public bool Spike { get; init; } = true;

    /// <summary>The kubeconfig context; null takes <see cref="KubeClientFactory.ContextVariable"/> or the current one.</summary>
    public string? KubeContext { get; init; }

    /// <summary>The namespace's ResourceQuota, or null for none.</summary>
    public NamespaceQuota? Quota { get; init; }

    /// <summary>
    /// Use the existing namespace <c>&lt;prefix&gt;-&lt;run id&gt;</c> (checked to be the run's own) instead of
    /// creating it: no namespace or quota is created, and disposing removes only the nodes the run deployed
    /// (<see cref="Runner.AdoptedNamespace"/>). Needs <see cref="RunId"/>.
    /// </summary>
    public bool AdoptNamespace { get; init; }

    /// <summary>Keep the namespace when the run is disposed.</summary>
    public bool KeepNamespace { get; init; }

    /// <summary>
    /// Wait until the namespace is gone when the run is disposed. Off by default: disposing issues the deletion and
    /// returns, and the namespace finishes terminating in the background (19-62 s measured, the PVCs last). It still
    /// holds its slot under <see cref="MaxConcurrentRuns"/> until it is gone (<see cref="RunAdmission.HoldsSlot"/>),
    /// and the reaper deletes whatever a run that could not finish left behind.
    /// </summary>
    public bool WaitForDeletion { get; init; }

    /// <summary>
    /// The grace period, in seconds, the run's pods get when the run is disposed, instead of each pod's own (bitcoind
    /// 30 s, LND and CLN 15 s, CLN's drain): the run is over, so a graceful stop only delays the namespace's deletion.
    /// The disposal stops the pods first (about 3 s, <see cref="RunNamespace.StopPodsAsync"/>), then deletes the
    /// namespace, which then goes in seconds. Null keeps each pod's own and deletes the namespace at once (it then
    /// takes 30 s or more to go). Not for <see cref="AdoptNamespace"/> runs, whose nodes are removed one by one.
    /// </summary>
    public int? TeardownGracePeriodSeconds { get; init; } = 1;

    /// <summary>How long <see cref="WaitForDeletion"/> waits.</summary>
    public TimeSpan DeletionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Where the run writes what it does (namespace created, deleted, kept); null for nowhere.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The owner recorded on the namespace for the reaper; null for this process.</summary>
    public RunOwner? Owner { get; init; }

    /// <summary>The run's own TTL for the reaper (instead of <see cref="ReaperOptions.Ttl"/>); null for the reaper's.</summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>
    /// The cap on live run namespaces under the prefix (all processes, <see cref="RunAdmission"/>); null for none.
    /// </summary>
    public int? MaxConcurrentRuns { get; init; }

    /// <summary>How long a run waits for a slot under <see cref="MaxConcurrentRuns"/>.</summary>
    public TimeSpan AdmissionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Options for <paramref name="suite"/> with the environment applied: <see cref="TestRunId.EnvironmentVariable"/>,
    /// <see cref="NamespacePrefixVariable"/>, <see cref="KubeClientFactory.ContextVariable"/>,
    /// <see cref="KeepNamespaceVariable"/>, <see cref="AdoptNamespaceVariable"/> and
    /// <see cref="RunAdmission.MaxRunsVariable"/> (default <see cref="RunAdmission.DefaultMaxRuns"/>).
    /// </summary>
    public static TestRunOptions FromEnvironment(string suite, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var prefix = environment(NamespacePrefixVariable);
        var keep = environment(KeepNamespaceVariable);
        var adopt = environment(AdoptNamespaceVariable);
        return new TestRunOptions
        {
            Suite = suite,
            RunId = environment(TestRunId.EnvironmentVariable),
            NamespacePrefix = string.IsNullOrWhiteSpace(prefix) ? SpikeNamespacePrefix : prefix.Trim(),
            KubeContext = environment(KubeClientFactory.ContextVariable),
            KeepNamespace = IsSet(keep),
            AdoptNamespace = IsSet(adopt),
            MaxConcurrentRuns = RunAdmission.ParseMaxRuns(environment(RunAdmission.MaxRunsVariable))
        };
    }

    private static bool IsSet(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}