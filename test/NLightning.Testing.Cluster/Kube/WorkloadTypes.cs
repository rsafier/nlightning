using k8s.Models;

namespace NLightning.Testing.Cluster.Kube;

/// <summary>
/// A named container port; it is also published on the node's headless Service.
/// </summary>
public sealed record WorkloadPort(string Name, int Port, string Protocol = "TCP");

/// <summary>
/// A container's CPU and memory requests and limits (plan R4: the quota and the scheduler count them). Requests stay
/// at or below 1 CPU / 1 GiB per pod in the spike.
/// </summary>
public sealed record WorkloadResources(string CpuRequest, string MemoryRequest, string CpuLimit, string MemoryLimit)
{
    /// <summary>A Lightning node or bitcoind in a small topology.</summary>
    public static WorkloadResources Default { get; } = new("250m", "256Mi", "1", "1Gi");

    /// <summary>A helper container (busybox and the like).</summary>
    public static WorkloadResources Tiny { get; } = new("10m", "16Mi", "100m", "64Mi");

    internal V1ResourceRequirements ToKubernetes() =>
        new()
        {
            Requests = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new(CpuRequest),
                ["memory"] = new(MemoryRequest)
            },
            Limits = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new(CpuLimit),
                ["memory"] = new(MemoryLimit)
            }
        };
}

/// <summary>
/// The node's persistent data: a PVC from the StatefulSet's claim template, mounted at <paramref name="MountPath"/>.
/// It survives pod restarts and kills (plan: reestablish, data-loss and mid-splice restart proofs) and goes with the
/// namespace.
/// </summary>
/// <param name="MountPath">Where the node keeps its data (e.g. <c>/root/.lnd</c>).</param>
/// <param name="Size">The claim's size.</param>
/// <param name="StorageClassName">The storage class, or null for the cluster default (OrbStack: local-path).</param>
public sealed record DataVolume(string MountPath, string Size = "1Gi", string? StorageClassName = null)
{
    /// <summary>The claim template's (and volume's) name; the PVC is <c>data-&lt;node&gt;-0</c>.</summary>
    public const string VolumeName = "data";
}

/// <summary>
/// Readiness probe builders: a node's pod is ready only when its probe says the node itself is (plan: real readiness,
/// e.g. bitcoind's RPC answering, LND synced to chain), not when the process merely started.
/// </summary>
public static class Probes
{
    /// <summary>A probe that runs <paramref name="command"/> in the container; exit 0 means ready.</summary>
    public static V1Probe Exec(IReadOnlyList<string> command, int periodSeconds = 2, int timeoutSeconds = 5,
                               int failureThreshold = 3, int initialDelaySeconds = 0) =>
        new()
        {
            Exec = new V1ExecAction { Command = [.. command] },
            PeriodSeconds = periodSeconds,
            TimeoutSeconds = timeoutSeconds,
            FailureThreshold = failureThreshold,
            InitialDelaySeconds = initialDelaySeconds,
            SuccessThreshold = 1
        };

    /// <summary>A probe that opens a TCP connection to <paramref name="port"/>.</summary>
    public static V1Probe Tcp(int port, int periodSeconds = 2, int timeoutSeconds = 2, int failureThreshold = 3) =>
        new()
        {
            TcpSocket = new V1TCPSocketAction { Port = port },
            PeriodSeconds = periodSeconds,
            TimeoutSeconds = timeoutSeconds,
            FailureThreshold = failureThreshold,
            SuccessThreshold = 1
        };
}

/// <summary>
/// The objects of one node: its StatefulSet (one replica, PVC template) and its headless Service.
/// </summary>
public sealed record NodeWorkloadManifests(V1StatefulSet StatefulSet, V1Service Service);