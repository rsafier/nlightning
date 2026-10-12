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
/// <param name="Storage">
/// <see cref="NodeStorage.Persistent"/> (a PVC, the default) or <see cref="NodeStorage.Ephemeral"/> (an <c>emptyDir</c>:
/// no claim to bind and provision, but the data goes with the pod).
/// </param>
public sealed record DataVolume(string MountPath, string Size = "1Gi", string? StorageClassName = null,
                                NodeStorage Storage = NodeStorage.Persistent)
{
    /// <summary>The claim template's (and volume's) name; the PVC is <c>data-&lt;node&gt;-0</c>.</summary>
    public const string VolumeName = "data";

    /// <summary>Whether the data lives in an <c>emptyDir</c> (lost when the pod is replaced).</summary>
    public bool IsEphemeral => Storage == NodeStorage.Ephemeral;
}

/// <summary>
/// Where a node keeps its data directory. Measured on OrbStack (local-path, <c>WaitForFirstConsumer</c>): a PVC costs
/// each StatefulSet wave about 6 s before its container starts (binding, one scheduling retry, provisioning); an
/// <c>emptyDir</c> costs nothing. The PVC is what lets a node be restarted or killed and come back with its keys,
/// wallet and channels; an <c>emptyDir</c> survives only a crash in place (a container restart in the same pod).
/// </summary>
public enum NodeStorage
{
    /// <summary>A PVC from the StatefulSet's claim template: survives restarts, kills and crashes (the default).</summary>
    Persistent,

    /// <summary>
    /// An <c>emptyDir</c>: for nodes a test never restarts or kills (<see cref="Nodes.KubeNodeHandle"/> refuses
    /// both); a crash in place keeps it.
    /// </summary>
    Ephemeral
}

/// <summary>
/// The run-wide default of <see cref="NodeStorage"/> from <see cref="Variable"/> (<c>persistent</c> or
/// <c>ephemeral</c>), for builders that leave a node's storage unset.
/// </summary>
public static class NodeStorageEnvironment
{
    public const string Variable = "NLTG_NODE_STORAGE";

    /// <summary>The value of <see cref="Variable"/>, or null when it is unset.</summary>
    /// <exception cref="ArgumentException">The value is neither <c>persistent</c> nor <c>ephemeral</c>.</exception>
    public static NodeStorage? Read(Func<string, string?>? environment = null)
    {
        var value = (environment ?? Environment.GetEnvironmentVariable)(Variable);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "persistent" or "pvc" => NodeStorage.Persistent,
            "ephemeral" or "emptydir" => NodeStorage.Ephemeral,
            _ => throw new ArgumentException($"{Variable}='{value}' is neither 'persistent' nor 'ephemeral'",
                                             nameof(environment))
        };
    }
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