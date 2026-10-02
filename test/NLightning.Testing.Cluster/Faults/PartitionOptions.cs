namespace NLightning.Testing.Cluster.Faults;

/// <summary>
/// How <see cref="FaultInjector.PartitionAsync"/> cuts the network.
/// </summary>
public sealed record PartitionOptions
{
    /// <summary>
    /// Comma-separated CIDRs allowed to open connections into partitioned nodes (the test runner that drives them).
    /// On OrbStack the macOS host reaches pods from the pod network's first address, <c>192.168.194.0/32</c>.
    /// </summary>
    public const string RunnerCidrsVariable = "NLTG_RUNNER_CIDRS";

    /// <summary>The default options.</summary>
    public static PartitionOptions Default { get; } = new();

    /// <summary>
    /// Whether partitioned nodes still reach the cluster DNS (default true): the partition is about the peers, and a
    /// node that cannot resolve names fails differently (slow resolver timeouts) from one whose peers are unreachable.
    /// </summary>
    public bool AllowDns { get; init; } = true;

    /// <summary>The namespace of the cluster DNS pods.</summary>
    public string DnsNamespace { get; init; } = "kube-system";

    /// <summary>The labels of the cluster DNS pods (CoreDNS keeps the legacy <c>k8s-app=kube-dns</c>).</summary>
    public IReadOnlyDictionary<string, string> DnsPodLabels { get; init; } =
        new Dictionary<string, string> { ["k8s-app"] = "kube-dns" };

    /// <summary>
    /// CIDRs that may still open connections into the partitioned nodes (the replies flow back through connection
    /// tracking): the test runner, which must keep driving a node over gRPC or JSON-RPC while it is cut off from its
    /// peers. Empty by default; <see cref="FromEnvironment"/> reads <see cref="RunnerCidrsVariable"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedIngressCidrs { get; init; } = [];

    /// <summary>
    /// How long <see cref="FaultInjector.PartitionAsync"/> and <see cref="FaultInjector.HealAsync"/> wait after the
    /// API call for the network plugin to apply it (it is asynchronous; OrbStack's k3s policy controller applied both
    /// within a second in the spike's measurements).
    /// </summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The defaults with <see cref="AllowedIngressCidrs"/> from <see cref="RunnerCidrsVariable"/>.</summary>
    public static PartitionOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary><see cref="FromEnvironment()"/> over an environment lookup (for tests).</summary>
    public static PartitionOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var cidrs = environment(RunnerCidrsVariable);
        return new PartitionOptions
        {
            AllowedIngressCidrs = string.IsNullOrWhiteSpace(cidrs)
                                      ? []
                                      : cidrs.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                       | StringSplitOptions.TrimEntries)
        };
    }
}