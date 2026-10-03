namespace NLightning.Integration.Tests.Fixtures.Eclair;

using Cluster;
using Docker.Utils;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.Eclair;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;

/// <summary>
/// The cluster backend of <see cref="EclairFixture"/> (<c>NLTG_TEST_BACKEND=cluster</c>, test harness phase 4): a warm
/// topology (<see cref="ClusterTopologyFixture"/>, one run namespace for the whole Eclair collection) with the harness's
/// bitcoind <c>miner</c> on Bitcoin Core 31.1 (as the Docker backend's <see cref="InteropChainHost"/>; Eclair 0.14.3
/// refuses older) on <c>emptyDir</c>, and Eclair <see cref="EclairFixture.EclairContainerName"/> from the same local
/// image, configuration and wallet (<see cref="EclairNode"/>) on a PVC behind its stable ClusterIP name
/// (<see cref="StableNodeAddress"/>), because the tests restart it.
/// </summary>
/// <remarks>
/// The in-process nodes reach bitcoind by its pod IP (<see cref="ClusterChainEndpoint"/>), Eclair's p2p port at the
/// stable name (it stays the same across <see cref="RestartEclairAsync"/>, as the Docker backend's fixed host port
/// does) and its API at the pod's IP (moved to the new pod after a restart). Eclair reaches this process at
/// <see cref="HostEndpoints.ForPods"/>. A failed test of the collection dumps the namespace, Eclair's state included
/// (<c>[assembly: ClusterDiagnostics]</c>).
/// </remarks>
public sealed class ClusterEclairBackend : IEclairBackend
{
    /// <summary>How long Eclair may take to be ready again, reachable, and at the tip.</summary>
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly EclairTopology _topology = new();

    private RegtestBitcoinEndpoint? _bitcoin;
    private EclairClient? _eclair;
    private EclairTestPeer? _peer;

    public TestBackendKind Kind => TestBackendKind.Cluster;

    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException("The Eclair fixture is not running");

    public EclairClient Eclair => _eclair ?? throw new InvalidOperationException("The Eclair fixture is not running");

    public string EclairNodeId { get; private set; } = string.Empty;

    public string EclairHost { get; private set; } = string.Empty;

    public int EclairPort => EclairNode.P2PPort;

    public string HostAddressForPeers { get; } = HostEndpoints.ForPods();

    /// <summary>The run (namespace) of the collection.</summary>
    public TestRun Run => _topology.Run;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _topology.InitializeAsync();
        foreach (var line in _topology.StartLog)
            Console.WriteLine(line);

        _bitcoin = ClusterChainEndpoint.Create(_topology.Topology.Chain);
        _peer = _topology.Node<EclairTestPeer>(EclairFixture.EclairContainerName);
        _eclair = new EclairClient(_peer.Api.BaseAddress, EclairFixture.ApiPassword);
        EclairNodeId = await _peer.GetNodeIdAsync(cancellationToken);
        // The stable ClusterIP name: our nodes store it and redial it after Eclair's restart
        EclairHost = _peer.P2PHost;
        await WaitReachableAsync(EclairHost, EclairPort, cancellationToken);
        Console.WriteLine($"[eclair] {EclairNodeId}@{EclairHost}:{EclairPort} (API {_peer.Api.BaseAddress}): "
                        + (await _eclair.GetInfoAsync(cancellationToken)).ToJsonString());
    }

    public async Task RestartEclairAsync(CancellationToken cancellationToken)
    {
        var peer = _peer ?? throw new InvalidOperationException("The Eclair fixture is not running");
        // Graceful: the preStop drains the stable Service, then the JVM stops on SIGTERM; the new pod uses the same PVC
        await peer.RestartAsync(s_readyTimeout, cancellationToken);
        Eclair.Retarget(peer.Api.BaseAddress);
        await Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            var tip = await Bitcoin.Rpc.GetBlockCountAsync(ct);
            var height = await Eclair.GetBlockHeightAsync(ct);
            return height == tip ? null : $"Eclair at {height}, tip {tip}";
        }, s_readyTimeout, $"{peer} at the tip after its restart", cancellationToken);
        await WaitReachableAsync(EclairHost, EclairPort, cancellationToken);
    }

    public async Task DumpEclairLogAsync(int tail)
    {
        if (_peer is null)
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var log = await _peer.Node.ReadLogAsync(tail, timeout.Token);
            Console.WriteLine($"===== kubectl logs {_peer.Node.Namespace}/{_peer.Node.PodName} (last {tail} lines) =====");
            Console.WriteLine(log);
        }
        catch (Exception e)
        {
            Console.WriteLine($"===== kubectl logs {_peer.Node.Namespace}/{_peer.Node.PodName}: unavailable ({e.Message}) =====");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _eclair?.Dispose();
        await _topology.DisposeAsync();
    }

    /// <summary>
    /// Until a TCP connect to <paramref name="host"/>:<paramref name="port"/> from this process succeeds: a new
    /// ClusterIP is routed only 4-9 s after its Service was created (spike check 1), and a restarted node's only once
    /// its new pod is ready.
    /// </summary>
    private static Task WaitReachableAsync(string host, int port, CancellationToken cancellationToken) =>
        Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            var probe = await TcpProbe.ProbeAsync(host, port, TimeSpan.FromSeconds(2), null, ct);
            return probe.Succeeded ? null : $"connect failed: {probe.Error}";
        }, s_readyTimeout, $"{host}:{port} reachable from the test process", cancellationToken,
                                            TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// The collection's topology: bitcoind <c>miner</c> (31.1, <c>emptyDir</c>) and Eclair
    /// <see cref="EclairFixture.EclairContainerName"/> (PVC, restarted by the tests), nothing funded or opened (the
    /// tests build their own channels, as on Docker).
    /// </summary>
    private sealed class EclairTopology : ClusterTopologyFixture
    {
        protected override string Suite => "eclair-interop";

        protected override void Configure(TopologyBuilder builder)
        {
            builder.Storage = NodeStorage.Ephemeral;
            builder.AddBitcoinCore("miner", ImageVersions.BitcoinCore31)
                   .AddEclair(EclairFixture.EclairContainerName, storage: NodeStorage.Persistent);
        }
    }
}