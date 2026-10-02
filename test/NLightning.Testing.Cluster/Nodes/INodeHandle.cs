namespace NLightning.Testing.Cluster.Nodes;

using Kube;

/// <summary>
/// A deployed node of a run: where it is, how to run commands in it, and its lifecycle (plan R11). Implementation
/// clients (LND gRPC, CLN JSON-RPC, ...) and the <see cref="ILightningTestPeer"/> adapters are built on top of it.
/// </summary>
public interface INodeHandle
{
    /// <summary>The node's alias in its run (<c>alice</c>, <c>miner</c>): StatefulSet, Service and container name.</summary>
    string Name { get; }

    NodeKind Kind { get; }

    /// <summary>The run's namespace.</summary>
    string Namespace { get; }

    /// <summary>The pod (<c>&lt;name&gt;-0</c>; the same after every restart).</summary>
    string PodName { get; }

    /// <summary>The main container (equal to <see cref="Name"/>).</summary>
    string ContainerName { get; }

    /// <summary>
    /// The headless Service's DNS name (<c>&lt;name&gt;.&lt;ns&gt;.svc.cluster.local</c>). Inside the namespace the
    /// bare <see cref="Name"/> resolves too.
    /// </summary>
    string ServiceDnsName { get; }

    /// <summary>The pod's stable DNS name (<c>&lt;name&gt;-0.&lt;name&gt;.&lt;ns&gt;.svc.cluster.local</c>).</summary>
    string PodDnsName { get; }

    /// <summary>
    /// The pod IP seen at the last wait, or null before one. It may change on restart; peers should use the DNS names.
    /// </summary>
    string? PodIp { get; }

    /// <summary>Waits until the pod is ready (its readiness probe passes) and refreshes <see cref="PodIp"/>.</summary>
    Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Runs a command in the main container.</summary>
    Task<ExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken);

    /// <summary>Reads a file from the main container (binary safe).</summary>
    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Polls until a file the node writes at startup can be read (LND's <c>tls.cert</c>, <c>admin.macaroon</c>).</summary>
    Task<byte[]> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Writes a small file into the main container.</summary>
    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>The main container's log, for diagnostics.</summary>
    Task<string> ReadLogAsync(int? tailLines, CancellationToken cancellationToken);

    /// <summary>
    /// A graceful restart: the pod is deleted with its grace period and the StatefulSet recreates it under the same
    /// name with the same PVC; waits until the new pod is ready.
    /// </summary>
    Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// A hard stop: the pod is deleted with a 1 s grace period (SIGTERM, SIGKILL after 1 s; the replacement starts only
    /// once the old container is gone) and this waits until the new pod is ready. Not a crash (the process gets
    /// SIGTERM first): use <c>FaultInjector.CrashAsync</c> for one.
    /// </summary>
    Task KillAsync(TimeSpan readyTimeout, CancellationToken cancellationToken);
}