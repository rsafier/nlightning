using k8s;

namespace NLightning.Testing.Cluster.Nodes;

using Kube;

/// <summary>
/// The <see cref="INodeHandle"/> of a <see cref="NodeWorkload"/> deployed as a StatefulSet. Pause and network
/// partition (plan <c>Faults/</c>) are not here yet.
/// </summary>
public sealed class KubeNodeHandle : INodeHandle
{
    /// <summary>
    /// The grace period of <see cref="KillAsync"/>. Not 0: a grace-0 delete removes the pod object at once, so the
    /// StatefulSet started the replacement while the old container still ran on the same PVC (the kubelet still sends
    /// SIGTERM and waits its 2 s minimum; CLN then refused to start on its PID file lock, and two processes on one data
    /// directory risk corrupting it). With 1 s the pod object stays until its containers are gone: SIGTERM, SIGKILL
    /// after 1 s, then the replacement.
    /// </summary>
    public const int KillGracePeriodSeconds = 1;

    private readonly IKubernetes _client;
    private readonly int _gracePeriodSeconds;

    public KubeNodeHandle(IKubernetes client, string ns, string name, NodeKind kind, int gracePeriodSeconds = 10)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Namespace = KubeNames.RequireDns1123Label(ns, "namespace");
        Name = KubeNames.RequireDns1123Label(name, "node name", KubeNames.MaxWorkloadNameLength);
        Kind = kind;
        _gracePeriodSeconds = gracePeriodSeconds;
    }

    /// <summary>The client this handle drives, for implementation-specific calls.</summary>
    public IKubernetes Client => _client;

    public string Name { get; }

    public NodeKind Kind { get; }

    public string Namespace { get; }

    public string PodName => $"{Name}-0";

    public string ContainerName => Name;

    public string ServiceDnsName => $"{Name}.{Namespace}.svc.cluster.local";

    public string PodDnsName => $"{PodName}.{Name}.{Namespace}.svc.cluster.local";

    public string? PodIp { get; private set; }

    /// <summary>The UID of the pod seen at the last wait (a restart waits for another one).</summary>
    public string? PodUid { get; private set; }

    public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pod = await _client.WaitForPodReadyAsync(Namespace, PodName, timeout, cancellationToken)
                               .ConfigureAwait(false);
        PodIp = pod.Status.PodIP;
        PodUid = pod.Metadata.Uid;
    }

    public Task<ExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken) =>
        _client.ExecAsync(Namespace, PodName, ContainerName, command, cancellationToken);

    public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken) =>
        _client.ReadFileAsync(Namespace, PodName, ContainerName, path, cancellationToken);

    public Task<byte[]> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) =>
        _client.WaitForFileAsync(Namespace, PodName, ContainerName, path, timeout, cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        _client.WriteFileAsync(Namespace, PodName, ContainerName, path, content, cancellationToken);

    public Task<string> ReadLogAsync(int? tailLines, CancellationToken cancellationToken) =>
        _client.ReadLogAsync(Namespace, PodName, ContainerName, tailLines, cancellationToken);

    public Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) =>
        ReplacePodAsync(_gracePeriodSeconds, readyTimeout, cancellationToken);

    public Task KillAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) =>
        ReplacePodAsync(KillGracePeriodSeconds, readyTimeout, cancellationToken);

    public override string ToString() => $"{Namespace}/{Name}";

    private async Task ReplacePodAsync(int gracePeriodSeconds, TimeSpan readyTimeout,
                                       CancellationToken cancellationToken)
    {
        var previousUid = await _client.DeletePodAsync(Namespace, PodName, gracePeriodSeconds, cancellationToken)
                                       .ConfigureAwait(false);
        var pod = await _client.WaitForPodReadyAsync(Namespace, PodName, readyTimeout, cancellationToken,
                                                     previousUid)
                               .ConfigureAwait(false);
        PodIp = pod.Status.PodIP;
        PodUid = pod.Metadata.Uid;
    }
}