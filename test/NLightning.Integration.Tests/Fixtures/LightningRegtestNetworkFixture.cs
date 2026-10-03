using System.Diagnostics;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures;

using Lnd;

/// <summary>
/// The shared regtest network of the <c>regtest</c> collection (and of the <c>onchain-regtest</c> and
/// <c>gossip-regtest</c> collections, each with a network of its own): bitcoind (<c>miner</c>) and four LND nodes.
/// <c>alice</c>, <c>bob</c> and <c>carol</c> get LND-LND channels at startup; <c>david</c> has none, so the ABCD
/// tests can give him exactly the channels they need.
/// </summary>
/// <remarks>
/// <para>
/// Where it runs is <see cref="TestBackend"/>'s (<c>NLTG_TEST_BACKEND</c>, read once when xunit creates the fixture):
/// Docker by default (<see cref="DockerLndBackend"/>: LNUnit's <c>LNUnitBuilder</c> starts the containers), or a run
/// namespace of the Kubernetes harness (<see cref="ClusterLndBackend"/>: the warm <c>LndRegtestNetwork</c> with the same
/// nodes, flags, channels and policies, test harness phase 3). The tests see the same members either way; every LND
/// client is our own <see cref="LndNodeConnection"/> (<c>test/NLightning.Testing.Lnd</c>, LND 0.21.4 protos).
/// </para>
/// <para>
/// Our in-process nodes dial an LND node at <see cref="GetLndPeerEndpointAsync"/> (what
/// <c>NLightningTestNode.ConnectToAsync(LndNodeConnection)</c> does): its container IP on Docker, its Service name on
/// the cluster, so a restarted pod is redialled at its new IP (NL-780). LND dials us at <see cref="HostAddressForLnd"/>.
/// </para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public class LightningRegtestNetworkFixture : IAsyncLifetime
{
    /// <summary>
    /// Every container the Docker backend creates. They are force-removed before the network starts and when it is
    /// disposed. The cluster backend's pods have the same names, in a namespace of their own.
    /// </summary>
    public static readonly IReadOnlyList<string> ContainerNames = ["miner", "alice", "bob", "carol", "david"];

    /// <summary>
    /// The LND aliases, in <see cref="ContainerNames"/> order.
    /// </summary>
    public static readonly IReadOnlyList<string> LndAliases = ["alice", "bob", "carol", "david"];

    /// <summary>
    /// The LND image the four nodes run: <c>test/Docker/custom_lnd</c> built for its <c>LND_VERSION</c> under a
    /// versioned tag (never <c>custom_lnd:latest</c>, which other branches build for other LND versions).
    /// </summary>
    public const string LndImageName = "custom_lnd";

    /// <inheritdoc cref="LndImageName"/>
    public const string LndImageTag = "0.21.4-beta";

    private readonly ILndNetworkBackend _backend =
        TestBackend.Current == TestBackendKind.Cluster ? new ClusterLndBackend() : new DockerLndBackend();

    private readonly SharedObjectCache _shared = new();
    private int _disposed;

    /// <summary>Where the network runs (<see cref="TestBackend.Current"/> when xunit created the fixture).</summary>
    public TestBackendKind Backend => _backend.Kind;

    /// <summary>The cluster backend (its run, network and in-process deployer), or null on Docker.</summary>
    public ClusterLndBackend? Cluster => _backend as ClusterLndBackend;

    public RPCClient Bitcoin => _backend.Bitcoin;

    /// <summary>
    /// The ZMQ ports of the miner's raw block and raw tx feeds, on <see cref="Bitcoin"/>'s host.
    /// </summary>
    public (int RawBlockPort, int RawTxPort) BitcoinZmqPorts => _backend.BitcoinZmqPorts;

    /// <summary>
    /// The four LND nodes, in <see cref="LndAliases"/> order (all ready once the fixture started).
    /// </summary>
    public IReadOnlyList<LndNodeConnection> LndNodes => _backend.LndNodes;

    /// <summary>
    /// The host an LND node dials to reach a listener of this process: <c>HOST_ADDRESS</c> or
    /// <c>host.docker.internal</c> on Docker, <c>host.orb.internal</c> on OrbStack's cluster.
    /// </summary>
    public string HostAddressForLnd => _backend.HostAddressForPeers;

    /// <summary>
    /// The LND node with <paramref name="alias"/> (<c>alice</c>, <c>bob</c>, <c>carol</c> or <c>david</c>).
    /// </summary>
    /// <remarks>
    /// The connection stays the same across restarts: gRPC reconnects to the same address (Docker) or DNS name
    /// (cluster) and the restarted LND keeps its <c>tls.cert</c>, which the connection pins.
    /// </remarks>
    public LndNodeConnection GetLndNode(string alias) => _backend.GetLndNode(alias);

    /// <summary>
    /// The <c>host:port</c> an in-process node dials (and stores) to reach <paramref name="lnd"/>'s p2p port: the
    /// container IP on Docker, the Service name on the cluster (NL-780).
    /// </summary>
    public Task<string> GetLndPeerEndpointAsync(LndNodeConnection lnd, CancellationToken cancellationToken) =>
        _backend.GetLndPeerEndpointAsync(lnd, cancellationToken);

    /// <summary>
    /// Restarts the LND node <paramref name="alias"/> on its data. Docker: a plain container restart (same container,
    /// network and data); the caller waits for LND to be ready again. Cluster: a StatefulSet restart (same DNS name and
    /// PVC, a new pod IP) that returns once LND is <c>SERVER_ACTIVE</c>, its LND peers dialled it again and those
    /// channels are active.
    /// </summary>
    public Task RestartLndAsync(string alias) => _backend.RestartLndAsync(alias);

    /// <summary>
    /// Writes the last <paramref name="tail"/> log lines of the LND nodes <paramref name="aliases"/> to the test output
    /// (container logs on Docker, pod logs on the cluster, where a failed test also gets a full namespace dump).
    /// </summary>
    public Task DumpLndLogsAsync(IEnumerable<string> aliases, int tail = 300) =>
        _backend.DumpLndLogsAsync(aliases, tail);

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/>.
    /// Lets a test class build an expensive topology (nodes, channels) once and share it with the other tests of the
    /// collection. The object is disposed with the fixture if it is <see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/>. A failed creation is not cached, so the next caller tries again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is already used for another type.</exception>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await _backend.StartAsync(TestContext.Current.CancellationToken);
            // One comparable line per backend (test harness phase 3: fixture start, Docker against the cluster)
            Console.WriteLine($"[fixture] LND regtest network ({Backend}) ready in {watch.Elapsed.TotalSeconds:F1} s");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        GC.SuppressFinalize(this);
        _shared.DisposeAll();
        await _backend.DisposeAsync();
    }
}