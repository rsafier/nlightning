using System.Diagnostics;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures;

using Lnd;
using Testing.Cluster.Run;

/// <summary>
/// The shared regtest network of the <c>regtest</c> collection (and of the <c>onchain-regtest</c> and
/// <c>gossip-regtest</c> collections, each with a network of its own): bitcoind (<c>miner</c>) and four LND nodes.
/// <c>alice</c>, <c>bob</c> and <c>carol</c> get LND-LND channels at startup; <c>david</c> has none, so the ABCD
/// tests can give him exactly the channels they need.
/// </summary>
/// <remarks>
/// <para>
/// The network runs on the Kubernetes harness only (<see cref="ClusterLndBackend"/>: the warm <c>LndRegtestNetwork</c>
/// in a run namespace of its own, test harness phase 3). The Docker backend (LNUnit's <c>LNUnitBuilder</c>) was
/// retired with LNUnit (NL-820). Without <c>NLTG_TEST_BACKEND=cluster</c> the fixture starts nothing and every test of
/// its collections is skipped with the reason (<see cref="UnavailableReason"/>). With it, a missing Kubernetes
/// configuration is a fixture failure (<see cref="ConfigurationError"/>, thrown by <see cref="InitializeAsync"/>), never
/// a skip, so a cluster run without a cluster cannot pass as a suite of skipped tests (NL-860); run them with
/// <c>scripts/run-cluster.sh --matrix lnd,onchain,anchors,gossip,day0,abcd</c>. Every LND client is our own <see cref="LndNodeConnection"/> (<c>test/NLightning.Testing.Lnd</c>, LND 0.21.4 protos).
/// </para>
/// <para>
/// Our in-process nodes dial an LND node at <see cref="GetLndPeerEndpointAsync"/> (what
/// <c>NLightningTestNode.ConnectToAsync(LndNodeConnection)</c> does): its Service name, so a restarted pod is redialled
/// at its new IP (NL-780). LND dials us at <see cref="HostAddressForLnd"/>.
/// </para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public class LightningRegtestNetworkFixture : IAsyncLifetime
{
    /// <summary>
    /// The node names of the network, bitcoind first: the pods (StatefulSets and Services) of its run namespace.
    /// </summary>
    public static readonly IReadOnlyList<string> NodeNames = ["miner", "alice", "bob", "carol", "david"];

    /// <summary>
    /// The LND aliases, in <see cref="NodeNames"/> order.
    /// </summary>
    public static readonly IReadOnlyList<string> LndAliases = ["alice", "bob", "carol", "david"];

    /// <summary>
    /// The LND image the four nodes run: <c>test/Docker/custom_lnd</c> built for its <c>LND_VERSION</c> under a
    /// versioned tag (never <c>custom_lnd:latest</c>, which other branches build for other LND versions). The harness
    /// never pulls or builds it: build it once with
    /// <c>docker build -t custom_lnd:0.21.4-beta test/Docker/custom_lnd</c> (OrbStack's cluster shares the Docker
    /// image store).
    /// </summary>
    public const string LndImageName = "custom_lnd";

    /// <inheritdoc cref="LndImageName"/>
    public const string LndImageTag = "0.21.4-beta";

    private readonly ClusterLndBackend? _backend;
    private readonly Action<string> _skip;
    private readonly SharedObjectCache _shared = new();
    private int _disposed;

    public LightningRegtestNetworkFixture()
        : this(Environment.GetEnvironmentVariable, KubeConfigurationProbe)
    {
    }

    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">
    /// Throws when no Kubernetes configuration can be built (no kubeconfig, or in a pod without its service account
    /// token); called only on the cluster backend, where it is a fixture failure (<see cref="ConfigurationError"/>).
    /// </param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    internal LightningRegtestNetworkFixture(Func<string, string?> environment, Action kubeConfiguration,
                                            Action<string>? skip = null)
    {
        _skip = skip ?? (reason => Assert.Skip(reason));
        UnavailableReason = GetUnavailableReason(environment);
        if (UnavailableReason is not null)
            return;

        ConfigurationError = GetConfigurationError(kubeConfiguration);
        if (ConfigurationError is null)
            _backend = new ClusterLndBackend();
    }

    /// <summary>
    /// Why the network does not run in this process (the skip reason of every test that uses it): the backend is not
    /// the cluster. Null on the cluster backend, where the network runs or the fixture fails
    /// (<see cref="ConfigurationError"/>).
    /// </summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// On the cluster backend, why no Kubernetes configuration could be built; <see cref="InitializeAsync"/> then throws
    /// it, so every test of the collections fails as a fixture failure instead of skipping (NL-860). Null otherwise.
    /// </summary>
    public string? ConfigurationError { get; }

    /// <summary>The cluster backend (its run, network and in-process deployer).</summary>
    public ClusterLndBackend Cluster => Backend;

    public RPCClient Bitcoin => Backend.Bitcoin;

    /// <summary>
    /// The ZMQ ports of the miner's raw block and raw tx feeds, on <see cref="Bitcoin"/>'s host.
    /// </summary>
    public (int RawBlockPort, int RawTxPort) BitcoinZmqPorts => Backend.BitcoinZmqPorts;

    /// <summary>
    /// The four LND nodes, in <see cref="LndAliases"/> order (all ready once the fixture started).
    /// </summary>
    public IReadOnlyList<LndNodeConnection> LndNodes => Backend.LndNodes;

    /// <summary>
    /// The host an LND node dials to reach a listener of this process: <c>host.orb.internal</c> on OrbStack's
    /// cluster, or <c>NLTG_HOST_ADDRESS</c>.
    /// </summary>
    public string HostAddressForLnd => Backend.HostAddressForPeers;

    /// <summary>
    /// The LND node with <paramref name="alias"/> (<c>alice</c>, <c>bob</c>, <c>carol</c> or <c>david</c>).
    /// </summary>
    /// <remarks>
    /// The connection stays the same across restarts: gRPC reconnects to the same DNS name and the restarted LND keeps
    /// its <c>tls.cert</c>, which the connection pins.
    /// </remarks>
    public LndNodeConnection GetLndNode(string alias) => Backend.GetLndNode(alias);

    /// <summary>
    /// The <c>host:port</c> an in-process node dials (and stores) to reach <paramref name="lnd"/>'s p2p port: the
    /// Service name (NL-780).
    /// </summary>
    public Task<string> GetLndPeerEndpointAsync(LndNodeConnection lnd, CancellationToken cancellationToken) =>
        Backend.GetLndPeerEndpointAsync(lnd, cancellationToken);

    /// <summary>
    /// Restarts the LND node <paramref name="alias"/> on its data: a StatefulSet restart (same DNS name and PVC, a new
    /// pod IP) that returns once LND is <c>SERVER_ACTIVE</c>, its LND peers dialled it again and those channels are
    /// active.
    /// </summary>
    public Task RestartLndAsync(string alias) => Backend.RestartLndAsync(alias);

    /// <summary>
    /// Writes the last <paramref name="tail"/> log lines of the LND nodes <paramref name="aliases"/> to the test output
    /// (pod logs; a failed test also gets a full namespace dump). Does nothing when the network is not running.
    /// </summary>
    public Task DumpLndLogsAsync(IEnumerable<string> aliases, int tail = 300) =>
        _backend?.DumpLndLogsAsync(aliases, tail) ?? Task.CompletedTask;

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/>.
    /// Lets a test class build an expensive topology (nodes, channels) once and share it with the other tests of the
    /// collection. The object is disposed with the fixture if it is <see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/>. A failed creation is not cached, so the next caller tries again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is already used for another type.</exception>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        SkipIfUnavailable();
        return _shared.GetOrCreateAsync(key, factory);
    }

    /// <summary>
    /// Skips the current test when the network cannot run in this process (<see cref="UnavailableReason"/>). Every
    /// member that needs the network calls it, so a test that reaches one is reported skipped, never passed.
    /// </summary>
    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
            _skip(UnavailableReason);
    }

    public async ValueTask InitializeAsync()
    {
        if (UnavailableReason is not null)
        {
            // Nothing to start: the tests skip on their first use of the fixture
            Console.WriteLine($"[fixture] LND regtest network not started: {UnavailableReason}");
            return;
        }

        if (_backend is null)
            throw new InvalidOperationException(ConfigurationError);

        var watch = Stopwatch.StartNew();
        try
        {
            await _backend.StartAsync(TestContext.Current.CancellationToken);
            Console.WriteLine($"[fixture] LND regtest network (cluster) ready in {watch.Elapsed.TotalSeconds:F1} s");
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
        if (_backend is not null)
            await _backend.DisposeAsync();
    }

    /// <summary>
    /// Why the network does not run under <paramref name="environment"/>: the backend is not the cluster; null when it
    /// is (a mistyped backend name throws, so a typo never turns a suite into skips).
    /// </summary>
    internal static string? GetUnavailableReason(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return TestBackend.Parse(environment(TestBackend.EnvironmentVariable)) != TestBackendKind.Cluster
                   ? "The LND regtest network runs on the cluster backend only (NL-820): set "
                   + $"{TestBackend.EnvironmentVariable}=cluster or run scripts/run-cluster.sh "
                   + "--matrix lnd,onchain,anchors,gossip,day0,abcd"
                   : null;
    }

    /// <summary>
    /// Why no Kubernetes configuration can be built (<paramref name="kubeConfiguration"/> throws), or null when it can.
    /// </summary>
    internal static string? GetConfigurationError(Action kubeConfiguration)
    {
        ArgumentNullException.ThrowIfNull(kubeConfiguration);
        try
        {
            kubeConfiguration();
            return null;
        }
        catch (Exception e)
        {
            return $"{TestBackend.EnvironmentVariable}=cluster, but no Kubernetes cluster is configured to run the LND "
                 + $"regtest network on ({e.GetType().Name}: {e.Message})";
        }
    }

    private static void KubeConfigurationProbe() => KubeClientFactory.BuildConfiguration();

    private ClusterLndBackend Backend
    {
        get
        {
            SkipIfUnavailable();
            return _backend ?? throw new InvalidOperationException(UnavailableReason ?? ConfigurationError);
        }
    }
}