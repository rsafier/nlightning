using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures.Lnd;

using Cluster;
using Docker.Utils;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using Testing.Cluster.Topology.Lnd;

/// <summary>
/// The backend of the LND regtest network (<c>NLTG_TEST_BACKEND=cluster</c>, test harness phase 3; the only one since
/// the Docker backend was retired with LNUnit, NL-820): a warm <see cref="LndRegtestNetworkFixture"/> (one run
/// namespace for the collection: bitcoind <c>miner</c> + alice, bob, carol and david on <c>custom_lnd:0.21.4-beta</c>
/// with the old Docker fixture's flags, channels and policies, each on a PVC so it can restart) seen through the
/// fixture's members, plus the seam that lets our in-process node join (<see cref="JoinInProcessNodeAsync"/>). bitcoind
/// is reached by its pod IP from the host (<see cref="ClusterChainEndpoint"/>), the LND nodes' gRPC by their pod DNS
/// names (the in-tree clients survive a restart). A failed test of the collection dumps the namespace
/// (<c>[assembly: ClusterDiagnostics]</c>).
/// </summary>
/// <remarks>
/// <para>
/// A restart (<see cref="RestartLndAsync"/>) is a StatefulSet restart: same pod name, DNS name and PVC, a new pod IP.
/// The network has LND peers and joined nodes dial the new IP or name and waits until those channels are active again,
/// so nothing like the retired Docker fixture's address-hold containers (NL-262) is needed. A node a test started by
/// itself (not through <see cref="JoinInProcessNodeAsync"/>) that dialled an LND node by IP keeps that stale IP: dial
/// the node's DNS name instead (<c>alias.namespace.svc.cluster.local</c>, <see cref="LndPeerHost"/>).
/// </para>
/// <para>
/// Our in-process nodes listen on loopback and are announced to the pods as <c>host.orb.internal</c> (OrbStack): they
/// dial out to the LND nodes (pods appear to them as 127.0.0.1 and are saved inbound-only, NL-497).
/// </para>
/// </remarks>
public sealed class ClusterLndBackend : IAsyncDisposable
{
    private readonly NetworkFixture _fixture;
    private RegtestBitcoinEndpoint? _bitcoin;

    /// <param name="options">The network (<see cref="LndRegtestNetworkOptions"/>'s defaults when null).</param>
    /// <param name="deployer">The deployer of the in-process nodes that join; a default one when null.</param>
    /// <param name="environment">
    /// Reads environment variables (<see cref="HostEndpoints.ForPods"/>'s override); the process environment when null.
    /// </param>
    public ClusterLndBackend(LndRegtestNetworkOptions? options = null, InProcessNodeDeployer? deployer = null,
                             Func<string, string?>? environment = null)
    {
        HostAddressForPeers = HostEndpoints.ForPods(environment);
        Deployer = deployer ?? new InProcessNodeDeployer();
        _fixture = new NetworkFixture(options ?? new LndRegtestNetworkOptions(), Deployer);
    }

    /// <summary>The network (nodes, channels, restart, join, routed payments).</summary>
    public LndRegtestNetwork Network => _fixture.Network;

    /// <summary>The run (namespace) of the collection.</summary>
    public TestRun Run => _fixture.Run;

    /// <summary>The deployer of the in-process nodes that joined; the backend stops them before the namespace goes.</summary>
    public InProcessNodeDeployer Deployer { get; }

    /// <summary>bitcoind as an in-process node needs it (RPC with the <c>miner</c> wallet, ZMQ), by pod IP.</summary>
    public RegtestBitcoinEndpoint BitcoinEndpoint =>
        _bitcoin ?? throw new InvalidOperationException("The LND network is not running");

    public RPCClient Bitcoin => BitcoinEndpoint.Rpc;

    public (int RawBlockPort, int RawTxPort) BitcoinZmqPorts => (BitcoinEndpoint.ZmqBlockPort, BitcoinEndpoint.ZmqTxPort);

    public IReadOnlyList<LndNodeConnection> LndNodes => Network.LndNodes;

    /// <summary>How long the namespace and the network took to start.</summary>
    public TimeSpan StartTime => _fixture.StartTime;

    /// <summary>What the start logged (each step with its time).</summary>
    public IReadOnlyCollection<string> StartLog => _fixture.StartLog;

    /// <summary>
    /// <see cref="HostEndpoints.ForPods"/>: <c>host.orb.internal</c> on OrbStack (or <c>NLTG_HOST_ADDRESS</c>); a listener
    /// on loopback is enough there.
    /// </summary>
    public string HostAddressForPeers { get; }

    public LndNodeConnection GetLndNode(string alias) => Network.GetLndNode(alias);

    /// <summary>
    /// <see cref="LndPeerHost"/> of the node behind <paramref name="lnd"/> and LND's p2p port: the Service name, which
    /// our node stores and redials after a restart of the pod (a pod IP would go stale, NL-780).
    /// </summary>
    public Task<string> GetLndPeerEndpointAsync(LndNodeConnection lnd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lnd);
        cancellationToken.ThrowIfCancellationRequested();
        var node = Network.Nodes.FirstOrDefault(n => ReferenceEquals(n.Connection, lnd))
                ?? Network.Node(lnd.LocalAlias);
        return Task.FromResult($"{LndPeerHost(node.Alias)}:{LndWorkload.P2pPort}");
    }

    public async Task DumpLndLogsAsync(IEnumerable<string> aliases, int tail)
    {
        foreach (var alias in aliases)
        {
            var handle = _bitcoin is not null ? Network.Node(alias).Handle : null;
            if (handle is null)
                continue;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var log = await handle.ReadLogAsync(tail, timeout.Token);
                Console.WriteLine($"===== kubectl logs {handle.Namespace}/{handle.PodName} (last {tail} lines) =====");
                Console.WriteLine(log);
            }
            catch (Exception e)
            {
                Console.WriteLine($"===== kubectl logs {handle.Namespace}/{handle.PodName}: unavailable ({e.Message}) =====");
            }
        }
    }

    /// <summary>
    /// The host a node of this process dials to reach the LND node <paramref name="alias"/>: its Service's DNS name,
    /// which follows the pod after a restart (a pod IP does not).
    /// </summary>
    public string LndPeerHost(string alias) => Run.Identity.ServiceDnsName(Network.Node(alias).Alias);

    public Task RestartLndAsync(string alias) => Network.RestartAsync(alias, kill: false);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _fixture.EnsureStartedAsync(cancellationToken);
        foreach (var line in _fixture.StartLog)
            Console.WriteLine(line);
        _bitcoin = ClusterChainEndpoint.Create(Network.Chain);
    }

    /// <summary>
    /// Starts our in-process node <paramref name="name"/> on the network's chain (<see cref="Deployer"/>), waits until
    /// it is at the tip and funds its wallet with <paramref name="fundSat"/> (confirmed). <paramref name="settings"/>
    /// are <c>Section:Key=value</c> configuration entries. Open its channels with
    /// <see cref="LndRegtestNetwork.OpenChannelAsync"/> (our node dials).
    /// </summary>
    public async Task<InProcessNode> JoinInProcessNodeAsync(string name, long fundSat,
                                                            CancellationToken cancellationToken,
                                                            params string[] settings)
    {
        var node = await Network.JoinAsync(Deployer, new TopologyNodeSpec(name, NodeKind.NLightning, null, settings),
                                           fundSat, cancellationToken);
        return (InProcessNode)node;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>The warm network with the in-process deployer stopped before the namespace is deleted.</summary>
    private sealed class NetworkFixture(LndRegtestNetworkOptions options, InProcessNodeDeployer deployer)
        : LndRegtestNetworkFixture
    {
        protected override LndRegtestNetworkOptions CreateNetworkOptions() => options;

        protected override async ValueTask OnStoppingAsync()
        {
            try
            {
                await deployer.DisposeAsync();
            }
            finally
            {
                await base.OnStoppingAsync();
            }
        }
    }
}