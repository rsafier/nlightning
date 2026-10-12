namespace NLightning.Integration.Tests.Fixtures.Ldk;

using Cluster;
using Docker.Utils;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Ldk;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;

/// <summary>
/// The backend of <see cref="LdkFixture"/> (<c>NLTG_TEST_BACKEND=cluster</c>, test harness phase 4; the only one since
/// NL-866 retired the Docker backend): a warm
/// topology (<see cref="ClusterTopologyFixture"/>, one run namespace for the whole LDK collection) with the harness's
/// bitcoind (<c>miner</c>, Bitcoin Core 31.1, on <c>emptyDir</c>) and an ldk-server named
/// <see cref="LdkFixture.LdkContainerName"/> (the local image <c>nltg-ldk-server:dc02b76c</c>, built from <c>test/Docker/ldk_server</c> and never pulled,
/// with its configuration and alias) on
/// a PVC, so <see cref="RestartLdkAsync"/> keeps its node id and channels. The in-process nodes reach bitcoind by its
/// pod IP (<see cref="ClusterChainEndpoint"/>) and LDK at its stable ClusterIP (<see cref="StableNodeAddress"/>), which
/// LDK also announces and which survives the restart; LDK reaches them at
/// <see cref="HostEndpoints.ForPods"/>. A failed test of the collection dumps the namespace
/// (<c>[assembly: ClusterDiagnostics]</c>).
/// </summary>
public sealed class ClusterLdkBackend
{
    /// <summary>How long LDK may take to be ready again after a restart, and to reach the tip.</summary>
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly LdkTopology _topology = new();

    private RegtestBitcoinEndpoint? _bitcoin;
    private LdkClient? _ldk;
    private KubeNodeHandle? _ldkHandle;

    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException("The LDK fixture is not running");

    public LdkClient Ldk => _ldk ?? throw new InvalidOperationException("The LDK fixture is not running");

    public string LdkHost { get; private set; } = string.Empty;

    public int LdkPort => LdkNode.P2PPort;

    public string HostAddressForPeers { get; } = HostEndpoints.ForPods();

    /// <summary>The run (namespace) of the collection.</summary>
    public TestRun Run => _topology.Run;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _topology.EnsureStartedAsync(cancellationToken);
        foreach (var line in _topology.StartLog)
            Console.WriteLine(line);

        _bitcoin = ClusterChainEndpoint.Create(_topology.Topology.Chain);
        var peer = _topology.Node<LdkTestPeer>(LdkFixture.LdkContainerName);
        _ldkHandle = (KubeNodeHandle)peer.Node;
        _ldk = new LdkClient(LdkFixture.LdkContainerName, LdkNode.ConfigPath, KubeExec(_ldkHandle));
        LdkHost = peer.P2PHost;
        await WaitReachableAsync(LdkHost, LdkPort, cancellationToken);
        await WaitAtTipAsync(cancellationToken);
    }

    /// <summary>
    /// A graceful restart of LDK's pod (the drain, then SIGTERM; the new pod starts on the same PVC and keeps the
    /// ClusterIP), then until LDK answers at the tip.
    /// </summary>
    public async Task RestartLdkAsync(CancellationToken cancellationToken)
    {
        var handle = _ldkHandle ?? throw new InvalidOperationException("The LDK fixture is not running");
        await handle.RestartAsync(s_readyTimeout, cancellationToken);
        await WaitAtTipAsync(cancellationToken);
    }

    public async Task DumpLdkLogAsync(int tail)
    {
        if (_ldkHandle is null)
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var log = await _ldkHandle.ReadLogAsync(tail, timeout.Token);
            Console.WriteLine($"===== kubectl logs {_ldkHandle.Namespace}/{_ldkHandle.PodName} (last {tail} lines) =====");
            Console.WriteLine(log);
        }
        catch (Exception e)
        {
            Console.WriteLine($"===== kubectl logs {_ldkHandle.Namespace}/{_ldkHandle.PodName}: unavailable ({e.Message}) =====");
        }
    }

    public async ValueTask DisposeAsync() => await _topology.DisposeAsync();

    /// <summary>An <see cref="LdkExec"/> over a Kubernetes exec in <paramref name="node"/>'s pod.</summary>
    public static LdkExec KubeExec(INodeHandle node) => async (command, cancellationToken) =>
    {
        var result = await node.ExecAsync(command, cancellationToken);
        return new LdkExecResult(result.ExitCode, result.StdOutText, result.StdErrText);
    };

    /// <summary>
    /// Until LDK's <c>current_best_block</c> is bitcoind's tip (LDK polls bitcoind's RPC every few seconds).
    /// </summary>
    private Task WaitAtTipAsync(CancellationToken cancellationToken)
    {
        var last = string.Empty;
        return Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            try
            {
                var height = await Ldk.GetBlockHeightAsync(ct);
                var tip = (uint)await Bitcoin.Rpc.GetBlockCountAsync(ct);
                return height == tip ? null : last = $"LDK at {height}, tip {tip}";
            }
            catch (LdkCliException e)
            {
                return last = e.Message;
            }
        }, s_readyTimeout, "LDK answers at the tip", cancellationToken, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// Until a TCP connect to <paramref name="host"/>:<paramref name="port"/> from this process succeeds: a new ClusterIP
    /// answers only 4-9 s after its Service was created (spike check 1).
    /// </summary>
    private static Task WaitReachableAsync(string host, int port, CancellationToken cancellationToken) =>
        Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            var probe = await TcpProbe.ProbeAsync(host, port, TimeSpan.FromSeconds(2), null, ct);
            return probe.Succeeded ? null : $"connect failed: {probe.Error}";
        }, s_readyTimeout, $"{host}:{port} reachable from the test process", cancellationToken,
                                            TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// The collection's topology: bitcoind <c>miner</c> (31.1, <c>emptyDir</c>) and ldk-server
    /// <see cref="LdkFixture.LdkContainerName"/> (alias <see cref="LdkFixture.LdkAlias"/>, on a PVC), nothing funded or
    /// opened (the tests build their own channels).
    /// </summary>
    private sealed class LdkTopology : ClusterTopologyFixture
    {
        protected override string Suite => "ldk-interop";

        protected override void Configure(TopologyBuilder builder)
        {
            builder.Storage = NodeStorage.Ephemeral;
            builder.AddBitcoinCore("miner", ImageVersions.BitcoinCore31)
                   .AddLdk(LdkFixture.LdkContainerName, extraArgs: [$"alias={LdkFixture.LdkAlias}"],
                           storage: NodeStorage.Persistent);
        }
    }
}