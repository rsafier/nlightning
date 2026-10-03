using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures;

using Lnd;

/// <summary>
/// The shared regtest network of the <c>regtest</c> collection: bitcoind (<c>miner</c>) and four LND nodes.
/// <c>alice</c>, <c>bob</c> and <c>carol</c> get LND-LND channels at startup; <c>david</c> has none, so the ABCD
/// tests can give him exactly the channels they need.
/// </summary>
/// <remarks>
/// Where the network runs is its <see cref="ILndNetworkBackend"/>'s: the Docker containers of
/// <see cref="DockerLndBackend"/> (LNUnit's builder, private to that backend; NL-819), or the cluster's
/// <see cref="ClusterLndBackend"/>. Every LND client the tests get is our own <see cref="LndNodeConnection"/>
/// (<c>test/NLightning.Testing.Lnd</c>, LND 0.21.4 protos).
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public class LightningRegtestNetworkFixture : IDisposable
{
    /// <summary>
    /// Every container the fixture creates. They are force-removed before the network starts and when it is disposed.
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

    private readonly ILndNetworkBackend _backend = new DockerLndBackend();
    private readonly SharedObjectCache _shared = new();

    public LightningRegtestNetworkFixture()
    {
        _backend.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public RPCClient Bitcoin => _backend.Bitcoin;

    /// <summary>
    /// The ZMQ ports of the miner's raw block and raw tx feeds, on <see cref="Bitcoin"/>'s host.
    /// </summary>
    public (int RawBlockPort, int RawTxPort) BitcoinZmqPorts => _backend.BitcoinZmqPorts;

    /// <summary>
    /// The four LND nodes, in <see cref="LndAliases"/> order (all ready once the fixture is constructed).
    /// </summary>
    public IReadOnlyList<LndNodeConnection> LndNodes => _backend.LndNodes;

    /// <summary>
    /// The LND node with <paramref name="alias"/> (<c>alice</c>, <c>bob</c>, <c>carol</c> or <c>david</c>); the same
    /// connection across restarts (<see cref="RestartLndAsync"/>).
    /// </summary>
    public LndNodeConnection GetLndNode(string alias) => _backend.GetLndNode(alias);

    /// <summary>
    /// Restarts the LND node <paramref name="alias"/> on its data. On Docker it is a plain container restart (same
    /// container, network and data) and the caller waits for LND to be ready again
    /// (<see cref="DockerLndBackend.RestartLndAsync"/>).
    /// </summary>
    public Task RestartLndAsync(string alias) => _backend.RestartLndAsync(alias);

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/>.
    /// Lets a test class build an expensive topology (nodes, channels) once and share it with the other tests of the
    /// collection. The object is disposed with the fixture if it is <see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/>. A failed creation is not cached, so the next caller tries again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is already used for another type.</exception>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _shared.DisposeAll();
        _backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}