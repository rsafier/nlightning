using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Faults;

using Kube;
using Nodes;
using Run;

/// <summary>
/// Injects faults into a run's nodes (plan R11, <c>Faults/</c>): restart and kill (the pod is replaced, its PVC data
/// stays), crash (SIGKILL in place), pause and resume (SIGSTOP/SIGCONT), and network partitions (NetworkPolicy).
/// Every fault is recorded in <see cref="Events"/>. Disposing resumes paused nodes and heals partitions; deleting the
/// run's namespace would remove both anyway.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here needs privileges beyond the run's namespace: no privileged containers, no node access, no container
/// runtime CLI. Pause and crash exec POSIX <c>sh</c> builtins in the node's main container and need the pod to share
/// its process namespace (<see cref="ProcessFaultSupport.WithProcessFaults"/>); partitions are NetworkPolicies and need
/// a network plugin that enforces them (OrbStack's k3s does, see <see cref="PartitionPolicy"/>).
/// </para>
/// <para>Which kill to use:
/// <list type="bullet">
///   <item><see cref="CrashAsync"/> is the real crash (SIGKILL, no shutdown code runs, the container restarts in
///   place). Use it for crash-recovery proofs.</item>
///   <item><see cref="KillAsync"/> deletes the pod with a 1 s grace period
///   (<see cref="KubeNodeHandle.KillGracePeriodSeconds"/>): SIGTERM, SIGKILL after 1 s, and the replacement starts
///   only once the old container is gone. It is a fast forced replacement, not a crash. (A grace-0 delete, the first
///   version, removed the API object at once, and the StatefulSet started the replacement while the old container
///   still ran on the same PVC: about 1.6 s of overlap measured on OrbStack.)</item>
///   <item><see cref="RestartAsync"/> is the graceful replacement (SIGTERM and the workload's grace period).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class FaultInjector : IAsyncDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_signalSettleTimeout = TimeSpan.FromSeconds(10);

    private readonly IKubernetes _client;
    private readonly RunIdentity _run;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<FaultEvent> _events = new();
    private readonly ConcurrentDictionary<string, INodeHandle> _paused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NetworkPartition> _partitions = new(StringComparer.Ordinal);
    private int _partitionSequence;
    private int _disposed;

    /// <summary>An injector for the nodes of <paramref name="run"/>'s namespace.</summary>
    /// <param name="client">The cluster client (the run's own; the injector does not dispose it).</param>
    /// <param name="run">The run: only its namespace and its nodes are touched.</param>
    /// <param name="log">Where to write one line per fault, or null.</param>
    public FaultInjector(IKubernetes client, RunIdentity run, Action<string>? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _log = log;
    }

    /// <summary>The faults injected so far, in order.</summary>
    public IReadOnlyList<FaultEvent> Events => [.. _events];

    /// <summary>The partitions in force.</summary>
    public IReadOnlyCollection<NetworkPartition> Partitions => [.. _partitions.Values];

    /// <summary>
    /// A graceful restart (<see cref="INodeHandle.RestartAsync"/>): the node gets SIGTERM and its grace period, the
    /// StatefulSet recreates the pod under the same name with the same PVC, and this waits until it is ready.
    /// </summary>
    public Task<NodeReplacement> RestartAsync(INodeHandle node, TimeSpan readyTimeout,
                                              CancellationToken cancellationToken) =>
        ReplaceAsync(node, FaultKind.Restart, n => n.RestartAsync(readyTimeout, cancellationToken), cancellationToken);

    /// <summary>
    /// Deletes the pod with a 1 s grace period (<see cref="INodeHandle.KillAsync"/>) and waits until the replacement
    /// is ready. See the class remarks: this still delivers SIGTERM; use <see cref="CrashAsync"/> for a crash.
    /// </summary>
    public Task<NodeReplacement> KillAsync(INodeHandle node, TimeSpan readyTimeout,
                                           CancellationToken cancellationToken) =>
        ReplaceAsync(node, FaultKind.Kill, n => n.KillAsync(readyTimeout, cancellationToken), cancellationToken);

    /// <summary>
    /// A crash: SIGKILL to every process of the node's main container. No shutdown code runs; the kubelet restarts the
    /// container in the same pod (same UID, IP and PVC; its restart count grows) and this waits until it is ready.
    /// A second crash within ten minutes waits for the kubelet's crash-loop back-off (10 s, then doubling).
    /// </summary>
    /// <exception cref="FaultNotSupportedException">The node's process is PID 1 (deploy it
    /// <see cref="ProcessFaultSupport.WithProcessFaults"/>).</exception>
    public async Task<NodeReplacement> CrashAsync(INodeHandle node, TimeSpan readyTimeout,
                                                  CancellationToken cancellationToken)
    {
        RequireOwn(node);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        var before = await ReadPodAsync(node, cancellationToken).ConfigureAwait(false);
        var restartsBefore = RestartCount(before, node.ContainerName);

        string? execError = null;
        try
        {
            var result = await node.ExecAsync(ContainerProcessScripts.Signal("KILL", true, false), cancellationToken)
                                   .ConfigureAwait(false);
            if (result.ExitCode == ContainerProcessScripts.MainProcessIsPid1ExitCode)
                throw NotSupported(node, "crash");
        }
        catch (Exception e) when (e is not FaultNotSupportedException and not OperationCanceledException)
        {
            // The exec runs in the container it kills: the runtime may tear the session down before it returns
            execError = e.Message;
        }

        var after = await WaitForPodAsync(node, before.Metadata.Uid,
                                          pod => RestartCount(pod, node.ContainerName) > restartsBefore
                                              && PodStatusReader.IsReady(pod),
                                          "restarted in place and ready", readyTimeout, execError,
                                          cancellationToken).ConfigureAwait(false);
        await node.WaitReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);

        var replacement = new NodeReplacement(node.Name, FaultKind.Crash, before.Metadata.Uid, after.Metadata.Uid,
                                              before.Status?.PodIP, after.Status?.PodIP, restartsBefore,
                                              RestartCount(after, node.ContainerName), watch.Elapsed);
        Record(started, FaultKind.Crash, node.Name, watch.Elapsed, Describe(replacement));
        return replacement;
    }

    /// <summary>
    /// A clean restart in place: runs <paramref name="stopCommand"/> in the node's main container (the node's own
    /// shutdown, e.g. <c>bitcoin-cli stop</c>), and waits until the kubelet has restarted the container in the same pod
    /// (same UID, IP and data, an <c>emptyDir</c> included; its restart count grows) and it is ready again. Unlike
    /// <see cref="CrashAsync"/> the node flushes its state; unlike <see cref="RestartAsync"/> it works on an
    /// <c>emptyDir</c> and keeps the pod IP. Needs no shared process namespace. Every connection to the node drops
    /// (established ones too, which a partition alone keeps). A second restart within ten minutes waits for the
    /// kubelet's back-off (10 s, then doubling).
    /// </summary>
    /// <exception cref="InvalidOperationException">The stop command failed.</exception>
    public async Task<NodeReplacement> RestartInPlaceAsync(INodeHandle node, IReadOnlyList<string> stopCommand,
                                                           TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        RequireOwn(node);
        ArgumentNullException.ThrowIfNull(stopCommand);
        if (stopCommand.Count == 0)
            throw new ArgumentException("The stop command is empty", nameof(stopCommand));

        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var before = await ReadPodAsync(node, cancellationToken).ConfigureAwait(false);
        var restartsBefore = RestartCount(before, node.ContainerName);

        var result = await node.ExecAsync(stopCommand, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"stopping {node} ({string.Join(' ', stopCommand)})");

        var after = await WaitForPodAsync(node, before.Metadata.Uid,
                                          pod => RestartCount(pod, node.ContainerName) > restartsBefore
                                              && PodStatusReader.IsReady(pod),
                                          "restarted in place and ready", readyTimeout, null,
                                          cancellationToken).ConfigureAwait(false);
        await node.WaitReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
        _paused.TryRemove(node.Name, out _);

        var replacement = new NodeReplacement(node.Name, FaultKind.RestartInPlace, before.Metadata.Uid,
                                              after.Metadata.Uid, before.Status?.PodIP, after.Status?.PodIP,
                                              restartsBefore, RestartCount(after, node.ContainerName), watch.Elapsed);
        Record(started, FaultKind.RestartInPlace, node.Name, watch.Elapsed, Describe(replacement));
        return replacement;
    }

    /// <summary>
    /// Pauses the node: SIGSTOP to every process of its main container, then waits until the kernel shows them
    /// stopped. The pod stays (no restart); its TCP peers see a silent node, and an exec readiness probe that talks
    /// to the node times out. Execs still work (a new process is not stopped).
    /// </summary>
    /// <exception cref="FaultNotSupportedException">The node's process is PID 1 (deploy it
    /// <see cref="ProcessFaultSupport.WithProcessFaults"/>).</exception>
    /// <exception cref="InvalidOperationException">The node is already paused by this injector.</exception>
    public Task<PausedNode> PauseAsync(INodeHandle node, CancellationToken cancellationToken) =>
        PauseAsync(node, null, cancellationToken);

    /// <summary>
    /// Pauses only the processes of the node's main container named <paramref name="processName"/> (their
    /// <c>/proc/&lt;pid&gt;/comm</c>): e.g. CLN's <c>lightningd</c> while its <c>connectd</c> keeps answering the
    /// transport, so a peer connects and exchanges <c>init</c> but nothing behind it moves.
    /// <see cref="ResumeAsync"/> continues every process of the container. Otherwise as
    /// <see cref="PauseAsync(INodeHandle, CancellationToken)"/>.
    /// </summary>
    public Task<PausedNode> PauseProcessAsync(INodeHandle node, string processName,
                                              CancellationToken cancellationToken)
    {
        if (!ContainerProcessScripts.IsProcessName(processName))
            throw new ArgumentException($"'{processName}' is not a process name", nameof(processName));
        return PauseAsync(node, processName, cancellationToken);
    }

    private async Task<PausedNode> PauseAsync(INodeHandle node, string? processName,
                                              CancellationToken cancellationToken)
    {
        RequireOwn(node);
        if (!_paused.TryAdd(node.Name, node))
            throw new InvalidOperationException($"{node} is already paused");

        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await node.ExecAsync(ContainerProcessScripts.Signal("STOP", true, true, passes: 3,
                                                                             processName: processName),
                                              cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == ContainerProcessScripts.MainProcessIsPid1ExitCode)
                throw NotSupported(node, "pause");
            result.EnsureSuccess($"SIGSTOP in {node}");

            var pids = ContainerProcessScripts.ParseSignalled(result.StdOutText);
            if (pids.Count == 0)
                throw new InvalidOperationException(
                    $"{node} has no process {(processName is null ? "" : $"named {processName} ")}to pause");

            await WaitForProcessesAsync(node, pids, p => p is null || p.IsStopped || p.HasExited, "stopped",
                                        cancellationToken).ConfigureAwait(false);

            var paused = new PausedNode(node.Name, pids, watch.Elapsed);
            Record(started, FaultKind.Pause, node.Name, watch.Elapsed,
                   $"SIGSTOP {(processName is null ? "" : processName + " ")}pids {string.Join(',', pids)}");
            return paused;
        }
        catch
        {
            _paused.TryRemove(node.Name, out _);
            await TryContinueAsync(node).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resumes a paused node: SIGCONT to every process of its main container (also ones paused by hand), then waits
    /// until none is stopped.
    /// </summary>
    public async Task ResumeAsync(INodeHandle node, CancellationToken cancellationToken)
    {
        RequireOwn(node);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        var result = await node.ExecAsync(ContainerProcessScripts.Signal("CONT", false, false), cancellationToken)
                               .ConfigureAwait(false);
        result.EnsureSuccess($"SIGCONT in {node}");
        var pids = ContainerProcessScripts.ParseSignalled(result.StdOutText);
        await WaitForProcessesAsync(node, pids, p => p is null || !p.IsStopped, "running", cancellationToken)
            .ConfigureAwait(false);

        _paused.TryRemove(node.Name, out _);
        Record(started, FaultKind.Resume, node.Name, watch.Elapsed, $"SIGCONT pids {string.Join(',', pids)}");
    }

    /// <summary>The processes of the node's main container (to observe a pause, or for diagnostics).</summary>
    public async Task<ContainerProcessList> ListProcessesAsync(INodeHandle node, CancellationToken cancellationToken)
    {
        RequireOwn(node);
        var result = await node.ExecAsync(ContainerProcessScripts.List(), cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"listing processes in {node}");
        return ContainerProcessScripts.ParseList(result.StdOutText);
    }

    /// <summary>
    /// Cuts <paramref name="isolated"/> off with a NetworkPolicy: from <paramref name="others"/> only, or, when it is
    /// null, from every pod but each other. New connections between the sides fail in both directions;
    /// <b>established connections survive on conntrack-based plugins (OrbStack)</b>, so disconnect the peers (the
    /// node's own command, or a restart) after this for the partition to bite. Waits
    /// <see cref="PartitionOptions.SettleTime"/> for the plugin to apply it.
    /// </summary>
    public async Task<NetworkPartition> PartitionAsync(IReadOnlyCollection<INodeHandle> isolated,
                                                       IReadOnlyCollection<INodeHandle>? others,
                                                       PartitionOptions? options,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isolated);
        foreach (var node in isolated.Concat(others ?? []))
            RequireOwn(node);
        options ??= PartitionOptions.Default;

        var name = NextPartitionName();
        var policy = PartitionPolicy.Build(_run, name, isolated.Select(n => n.Name).ToList(),
                                           others?.Select(n => n.Name).ToList(), options);
        var partition = new NetworkPartition(
            name, policy.Spec.PodSelector.MatchExpressions[0].Values.ToList(),
            others?.Select(n => n.Name).Distinct().Order(StringComparer.Ordinal).ToList(),
            others is null ? PartitionShape.Isolate : PartitionShape.Split);
        return await ApplyAsync(policy, partition, options.SettleTime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cuts <paramref name="nodes"/> off from everything outside the run's pods (<see cref="PartitionShape.Outside"/>):
    /// they keep their bitcoind, their peers in the run and DNS, and lose the host, i.e. the test process and the
    /// in-process nodes in it, both ways. The partition between a pod peer and our in-process node. As with every
    /// partition, established connections survive (disconnect the peers after this);
    /// <see cref="PartitionOptions.AllowedIngressCidrs"/> is ignored.
    /// </summary>
    public async Task<NetworkPartition> PartitionFromHostAsync(IReadOnlyCollection<INodeHandle> nodes,
                                                               PartitionOptions? options,
                                                               CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var node in nodes)
            RequireOwn(node);
        options ??= PartitionOptions.Default;

        var name = NextPartitionName();
        var policy = PartitionPolicy.BuildOutsideCut(_run, name, nodes.Select(n => n.Name).ToList(), options);
        var partition = new NetworkPartition(name, policy.Spec.PodSelector.MatchExpressions[0].Values.ToList(), null,
                                             PartitionShape.Outside);
        return await ApplyAsync(policy, partition, options.SettleTime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves only <paramref name="openPorts"/> open for new connections into <paramref name="node"/>, from anywhere
    /// (<see cref="PartitionShape.Ports"/>); the node's own connections out are untouched. E.g. bitcoind with its RPC
    /// and P2P ports open and its ZMQ feeds cut. Established connections survive: restart the node in place
    /// (<see cref="RestartInPlaceAsync"/>) or have the client reconnect for the cut to bite.
    /// </summary>
    public async Task<NetworkPartition> LimitIngressPortsAsync(INodeHandle node, IReadOnlyCollection<int> openPorts,
                                                               CancellationToken cancellationToken,
                                                               TimeSpan? settleTime = null)
    {
        RequireOwn(node);
        ArgumentNullException.ThrowIfNull(openPorts);

        var name = NextPartitionName();
        var policy = PartitionPolicy.BuildIngressPorts(_run, name, [node.Name], openPorts);
        var partition = new NetworkPartition(name, [node.Name], null, PartitionShape.Ports,
                                             openPorts.Distinct().Order().ToList());
        return await ApplyAsync(policy, partition, settleTime ?? PartitionOptions.Default.SettleTime,
                                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Isolates one node from every other pod (<see cref="PartitionAsync"/> with no other side).</summary>
    public Task<NetworkPartition> IsolateAsync(INodeHandle node, PartitionOptions? options,
                                               CancellationToken cancellationToken) =>
        PartitionAsync([node], null, options, cancellationToken);

    /// <summary>
    /// Ends a partition: deletes its NetworkPolicy and waits <paramref name="settleTime"/> (default 1 s) for the
    /// plugin. Connections cut before do not come back by themselves: the nodes reconnect as they normally would.
    /// </summary>
    public async Task HealAsync(NetworkPartition partition, CancellationToken cancellationToken,
                                TimeSpan? settleTime = null)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        await DeletePolicyAsync(partition.Name, cancellationToken).ConfigureAwait(false);
        _partitions.TryRemove(partition.Name, out _);
        await Task.Delay(settleTime ?? PartitionOptions.Default.SettleTime, cancellationToken).ConfigureAwait(false);

        Record(started, FaultKind.Heal, partition.Name, watch.Elapsed, partition.ToString());
    }

    /// <summary>Deletes every partition policy of the run (also ones another injector created).</summary>
    public async Task HealAllAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var list = await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(
                                    _run.Namespace, labelSelector: PartitionPolicy.Selector(_run.Id),
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
        foreach (var policy in list.Items)
            await DeletePolicyAsync(policy.Metadata.Name, cancellationToken).ConfigureAwait(false);
        _partitions.Clear();

        if (list.Items.Count > 0)
            Record(started, FaultKind.Heal, "*", watch.Elapsed,
                   string.Join(',', list.Items.Select(p => p.Metadata.Name)));
    }

    /// <summary>Resumes the nodes this injector paused and heals the run's partitions (best effort).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var node in _paused.Values)
            await TryContinueAsync(node).ConfigureAwait(false);
        _paused.Clear();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await HealAllAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpOperationException or OperationCanceledException or HttpRequestException)
        {
            _log?.Invoke($"[nltg-faults] healing partitions in {_run.Namespace} failed: {e.Message}");
        }
    }

    private string NextPartitionName() =>
        PartitionPolicy.NamePrefix + Interlocked.Increment(ref _partitionSequence).ToString(CultureInfo.InvariantCulture);

    private async Task<NetworkPartition> ApplyAsync(V1NetworkPolicy policy, NetworkPartition partition,
                                                    TimeSpan settleTime, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        await _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(policy, _run.Namespace,
                                                                      cancellationToken: cancellationToken)
                     .ConfigureAwait(false);
        _partitions[partition.Name] = partition;
        await Task.Delay(settleTime, cancellationToken).ConfigureAwait(false);

        Record(started, FaultKind.Partition, string.Join(',', partition.Isolated), watch.Elapsed,
               partition.ToString());
        return partition;
    }

    private async Task<NodeReplacement> ReplaceAsync(INodeHandle node, FaultKind kind, Func<INodeHandle, Task> act,
                                                     CancellationToken cancellationToken)
    {
        RequireOwn(node);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        var before = await _client.TryReadPodAsync(node.Namespace, node.PodName, cancellationToken)
                                  .ConfigureAwait(false);
        await act(node).ConfigureAwait(false);
        var after = await ReadPodAsync(node, cancellationToken).ConfigureAwait(false);

        // A paused node's processes died with its pod
        _paused.TryRemove(node.Name, out _);

        var replacement = new NodeReplacement(node.Name, kind, before?.Metadata.Uid, after.Metadata.Uid,
                                              before?.Status?.PodIP, after.Status?.PodIP,
                                              before is null ? 0 : RestartCount(before, node.ContainerName),
                                              RestartCount(after, node.ContainerName), watch.Elapsed);
        Record(started, kind, node.Name, watch.Elapsed, Describe(replacement));
        return replacement;
    }

    private async Task WaitForProcessesAsync(INodeHandle node, IReadOnlyList<int> pids,
                                             Func<ContainerProcess?, bool> condition, string what,
                                             CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + s_signalSettleTimeout;
        while (true)
        {
            var list = await ListProcessesAsync(node, cancellationToken).ConfigureAwait(false);
            var pending = pids.Where(pid => !condition(list.Find(pid))).ToList();
            if (pending.Count == 0)
                return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"{node}: pids {string.Join(',', pending)} not {what} after {s_signalSettleTimeout}: "
                  + string.Join("; ", list.Processes.Select(p => $"{p.Pid} {p.State} {p.Name}")));

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<V1Pod> WaitForPodAsync(INodeHandle node, string uid, Func<V1Pod, bool> condition,
                                              string what, TimeSpan timeout, string? context,
                                              CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var pod = await _client.TryReadPodAsync(node.Namespace, node.PodName, cancellationToken)
                                   .ConfigureAwait(false);
            if (pod is not null && pod.Metadata.Uid != uid)
                throw new InvalidOperationException($"{node}: the pod was replaced while waiting for it to be {what}");
            if (pod is not null && condition(pod))
                return pod;

            // CrashLoopBackOff is expected after a repeated crash: the kubelet starts the container after its back-off
            if (pod is not null && PodStatusReader.GetFatalReason(pod) is { } reason
                                && !reason.Contains("CrashLoopBackOff", StringComparison.Ordinal))
                throw new InvalidOperationException($"{node} will not be {what}: {reason}");

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"{node} not {what} after {timeout}: {PodStatusReader.Describe(pod)}"
                  + (context is null ? string.Empty : $" (exec: {context})"));

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<V1Pod> ReadPodAsync(INodeHandle node, CancellationToken cancellationToken) =>
        await _client.TryReadPodAsync(node.Namespace, node.PodName, cancellationToken).ConfigureAwait(false)
     ?? throw new InvalidOperationException($"{node} has no pod {node.PodName}");

    private async Task DeletePolicyAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(name, _run.Namespace,
                                                                          cancellationToken: cancellationToken)
                         .ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    private async Task TryContinueAsync(INodeHandle node)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await node.ExecAsync(ContainerProcessScripts.Signal("CONT", false, false), cts.Token)
                      .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log?.Invoke($"[nltg-faults] resuming {node} failed: {e.Message}");
        }
    }

    private void RequireOwn(INodeHandle node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!string.Equals(node.Namespace, _run.Namespace, StringComparison.Ordinal))
            throw new ArgumentException($"{node} is not in run {_run.Id}'s namespace {_run.Namespace}",
                                        nameof(node));
    }

    private void Record(DateTimeOffset started, FaultKind kind, string target, TimeSpan duration, string detail)
    {
        var fault = new FaultEvent(started, kind, target, duration, detail);
        _events.Enqueue(fault);
        _log?.Invoke($"[nltg-faults] {_run.Namespace}: {fault}");
    }

    private static int RestartCount(V1Pod pod, string container) =>
        pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == container)?.RestartCount ?? 0;

    private static string Describe(NodeReplacement r) =>
        $"pod {Short(r.PodUidBefore)} -> {Short(r.PodUidAfter)}, ip {r.PodIpBefore ?? "-"} -> {r.PodIpAfter ?? "-"}, "
      + $"restarts {r.RestartCountBefore} -> {r.RestartCountAfter}";

    private static string Short(string? uid) => uid is null ? "-" : uid.Length > 8 ? uid[..8] : uid;

    private static FaultNotSupportedException NotSupported(INodeHandle node, string what) =>
        new($"Cannot {what} {node}: its process is PID 1 of the pod, which ignores SIGSTOP/SIGKILL sent from inside. "
          + "Deploy it with NodeWorkload.WithProcessFaults() (shareProcessNamespace).");
}

/// <summary>
/// <see cref="FaultInjector"/> on a <see cref="TestRun"/>.
/// </summary>
public static class TestRunFaultExtensions
{
    /// <summary>A fault injector for the run's nodes, logging to <paramref name="log"/>.</summary>
    public static FaultInjector CreateFaultInjector(this TestRun run, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new FaultInjector(run.Client, run.Identity, log);
    }
}