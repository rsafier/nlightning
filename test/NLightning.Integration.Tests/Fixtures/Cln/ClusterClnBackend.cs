using System.Diagnostics;

namespace NLightning.Integration.Tests.Fixtures.Cln;

using Cluster;
using Docker.Utils;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Cln;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;

/// <summary>
/// The cluster backend of <see cref="ClnFixture"/> (<c>NLTG_TEST_BACKEND=cluster</c>, test harness phase 2): a warm
/// topology (<see cref="ClusterTopologyFixture"/>, one run namespace for the whole CLN collection) with the harness's
/// bitcoind (<c>miner</c>) and a CLN named <see cref="ClnFixture.ClnContainerName"/> (same image, flags and alias as
/// the Docker container), both on <c>emptyDir</c> (never restarted); the in-process nodes reach bitcoind by its pod
/// IP (<see cref="ClusterChainEndpoint"/>) and CLN by its pod IP, and CLN reaches them at
/// <see cref="HostEndpoints.ForPods"/>. A failed test of the collection dumps the namespace
/// (<c>[assembly: ClusterDiagnostics]</c>).
/// </summary>
/// <remarks>
/// Nodes a test adds (<see cref="StartClnAsync"/>) go into the same namespace and are removed when disposed
/// (<see cref="TestRun.RemoveNodeAsync"/>); a restartable one gets a PVC and its stable ClusterIP name
/// (<see cref="StableNodeAddress"/>), which this process dials, so its address survives the restart as the Docker
/// backend's fixed host port does.
/// </remarks>
public sealed class ClusterClnBackend : IClnBackend
{
    /// <summary>How long a node a test adds may take to be ready, and to be reachable at its address.</summary>
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly ClnTopology _topology = new();
    private readonly List<ClusterExtraClnNode> _extraNodes = [];

    private RegtestBitcoinEndpoint? _bitcoin;
    private ClnClient? _cln;
    private KubeNodeHandle? _clnHandle;

    public TestBackendKind Kind => TestBackendKind.Cluster;

    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException("The CLN fixture is not running");

    public ClnClient Cln => _cln ?? throw new InvalidOperationException("The CLN fixture is not running");

    public string ClnNodeId { get; private set; } = string.Empty;

    public string ClnHost { get; private set; } = string.Empty;

    public int ClnPort => ClnNode.P2PPort;

    public string HostAddressForPeers { get; } = HostEndpoints.ForPods();

    /// <summary>The run (namespace) of the collection.</summary>
    public TestRun Run => _topology.Run;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _topology.EnsureStartedAsync(cancellationToken);
        foreach (var line in _topology.StartLog)
            Console.WriteLine(line);

        _bitcoin = ClusterChainEndpoint.Create(_topology.Topology.Chain);
        var peer = _topology.Node<ClnTestPeer>(ClnFixture.ClnContainerName);
        _clnHandle = (KubeNodeHandle)peer.Node;
        _cln = new ClnClient(ClnFixture.ClnContainerName, KubeExec(_clnHandle));
        ClnNodeId = (await _cln.GetInfoAsync(cancellationToken))["id"]!.GetValue<string>();
        // Never restarted (emptyDir), so its pod IP stays; no DNS lookup and no ClusterIP routing delay
        ClnHost = _clnHandle.PodIp ?? throw new InvalidOperationException($"{_clnHandle} has no pod IP");
        await WaitReachableAsync(ClnHost, ClnPort, cancellationToken);
    }

    public async Task<ExtraClnNode> StartClnAsync(ClnNodeSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var watch = Stopwatch.StartNew();
        var run = _topology.Run;
        var chain = _topology.Topology.Chain;
        var options = new ClnNodeOptions
        {
            BitcoindHost = chain.RpcHost,
            BitcoindRpcPort = chain.RpcPort,
            BitcoindRpcUser = chain.RpcUser,
            BitcoindRpcPassword = chain.RpcPassword,
            Alias = spec.Name,
            LogLevel = "debug",
            EnforceFeeLimits = spec.EnforceFeeLimits,
            ExtraArgs = spec.ExtraArgs,
            // A restart keeps the node id and channels only on a PVC; the others start faster on an emptyDir
            Storage = spec.Restartable ? NodeStorage.Persistent : NodeStorage.Ephemeral
        };
        try
        {
            var handle = await run.DeployAsync(ClnNode.Workload(spec.Name, options), s_readyTimeout,
                                               cancellationToken);
            var host = spec.Restartable
                           ? await StableNodeAddress.EnsureAsync(run.Client, run.Identity, spec.Name, NodeKind.Cln,
                                                                 ClnNode.P2PPort, cancellationToken)
                           : handle.PodIp ?? throw new InvalidOperationException($"{handle} has no pod IP");
            var client = new ClnClient(spec.Name, KubeExec(handle));
            var nodeId = (await client.GetInfoAsync(cancellationToken))["id"]!.GetValue<string>();
            if (spec.ReachableFromTests)
                await WaitReachableAsync(host, ClnNode.P2PPort, cancellationToken);
            var node = new ClusterExtraClnNode(this, spec, client, nodeId, host, handle);
            lock (_extraNodes)
                _extraNodes.Add(node);
            Console.WriteLine($"[cluster] {run.Namespace}: CLN {spec.Name} {nodeId} at {host} ready in "
                            + $"{watch.Elapsed.TotalSeconds:F1} s");
            return node;
        }
        catch
        {
            await RemoveNodeQuietlyAsync(spec.Name);
            throw;
        }
    }

    public async Task DumpClnLogAsync(int tail)
    {
        if (_clnHandle is not null)
            await DumpPodLogAsync(_clnHandle, tail);
    }

    public async ValueTask DisposeAsync()
    {
        List<ClusterExtraClnNode> extraNodes;
        lock (_extraNodes)
            extraNodes = [.. _extraNodes];
        foreach (var node in extraNodes)
            await node.DisposeAsync();

        await _topology.DisposeAsync();
    }

    /// <summary>A <see cref="ClnExec"/> over a Kubernetes exec in <paramref name="node"/>'s pod.</summary>
    public static ClnExec KubeExec(INodeHandle node) => async (command, cancellationToken) =>
    {
        var result = await node.ExecAsync(command, cancellationToken);
        return new ClnExecResult(result.ExitCode, result.StdOutText, result.StdErrText);
    };

    private static async Task DumpPodLogAsync(INodeHandle node, int tail)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var log = await node.ReadLogAsync(tail, timeout.Token);
            Console.WriteLine($"===== kubectl logs {node.Namespace}/{node.PodName} (last {tail} lines) =====");
            Console.WriteLine(log);
        }
        catch (Exception e)
        {
            Console.WriteLine($"===== kubectl logs {node.Namespace}/{node.PodName}: unavailable ({e.Message}) =====");
        }
    }

    /// <summary>
    /// Until a TCP connect to <paramref name="host"/>:<paramref name="port"/> from this process succeeds: a pod IP
    /// answers at once, a new ClusterIP only 4-9 s after its Service was created (spike check 1).
    /// </summary>
    private static Task WaitReachableAsync(string host, int port, CancellationToken cancellationToken)
    {
        var last = string.Empty;
        return Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            var probe = await TcpProbe.ProbeAsync(host, port, TimeSpan.FromSeconds(2), null, ct);
            last = probe.Error ?? string.Empty;
            return probe.Succeeded ? null : $"connect failed: {last}";
        }, s_readyTimeout, $"{host}:{port} reachable from the test process", cancellationToken,
                                                       TimeSpan.FromMilliseconds(250));
    }

    private async Task RemoveNodeQuietlyAsync(string name)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _topology.Run.RemoveNodeAsync(name, timeout.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[cluster] removing {name} failed (the namespace's deletion removes it): {e.Message}");
        }
    }

    /// <summary>
    /// The collection's topology: bitcoind <c>miner</c> and CLN <see cref="ClnFixture.ClnContainerName"/>, both on
    /// <c>emptyDir</c>, nothing funded or opened (the tests build their own channels, as on Docker).
    /// </summary>
    private sealed class ClnTopology : ClusterTopologyFixture
    {
        protected override string Suite => "cln-interop";

        protected override void Configure(TopologyBuilder builder)
        {
            builder.Storage = NodeStorage.Ephemeral;
            builder.AddBitcoinCore("miner")
                   .AddCln(ClnFixture.ClnContainerName);
        }
    }

    private sealed class ClusterExtraClnNode(ClusterClnBackend backend, ClnNodeSpec spec, ClnClient client,
                                             string nodeId, string host, KubeNodeHandle handle)
        : ExtraClnNode(spec, client, nodeId, host, ClnNode.P2PPort)
    {
        private int _disposed;

        public override async Task RestartAsync(CancellationToken cancellationToken)
        {
            if (!Spec.Restartable)
                throw new InvalidOperationException($"{Name} was not started restartable (it has no PVC)");

            // Graceful: the preStop drains the stable Service, then stops lightningd; the new pod starts on the same PVC
            await handle.RestartAsync(s_readyTimeout, cancellationToken);
            await Testing.Cluster.Poll.UntilAsync(async ct =>
            {
                try
                {
                    await Client.GetInfoAsync(ct);
                    return true;
                }
                catch (Docker.Utils.ClnRpcException)
                {
                    return false;
                }
            }, s_readyTimeout, TimeSpan.FromMilliseconds(250), $"{Name} answers getinfo after its restart",
                                                cancellationToken);
        }

        public override Task DumpLogAsync(int tail) => DumpPodLogAsync(handle, tail);

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (backend._extraNodes)
                backend._extraNodes.Remove(this);
            await backend.RemoveNodeQuietlyAsync(Name);
        }
    }
}