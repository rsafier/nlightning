namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// The ResourceQuota of a run's namespace (plan R4): when the cluster is full, pods wait in <c>Pending</c> and the
/// scheduler is the semaphore. Every workload the harness builds declares requests and limits, which a quota on them
/// requires.
/// </summary>
/// <param name="RequestsCpu">The sum of the pods' CPU requests, e.g. <c>4</c>.</param>
/// <param name="RequestsMemory">The sum of the pods' memory requests, e.g. <c>8Gi</c>.</param>
/// <param name="LimitsCpu">The sum of the CPU limits, or null for none.</param>
/// <param name="LimitsMemory">The sum of the memory limits, or null for none.</param>
/// <param name="Pods">The most pods, or null for no cap.</param>
/// <param name="PersistentVolumeClaims">The most PVCs, or null for no cap.</param>
public sealed record NamespaceQuota(string RequestsCpu, string RequestsMemory, string? LimitsCpu = null,
                                    string? LimitsMemory = null, int? Pods = null, int? PersistentVolumeClaims = null)
{
    /// <summary>The quota object's name in the run's namespace.</summary>
    public const string ObjectName = "nltg-run-quota";

    /// <summary>A quota for a spike topology: 6 CPUs and 6 GiB of requests, 20 pods.</summary>
    public static NamespaceQuota Spike { get; } = new("6", "6Gi", "12", "12Gi", 20, 20);
}