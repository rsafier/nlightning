using Docker.DotNet;
using LNUnit.Setup;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// The shared regtest network of the <c>regtest</c> collection: bitcoind (<c>miner</c>) and four LND nodes.
/// <c>alice</c>, <c>bob</c> and <c>carol</c> get LND-LND channels at startup; <c>david</c> has none, so the ABCD
/// tests can give him exactly the channels they need.
/// </summary>
/// <remarks>
/// LNUnit's <c>LNUnitBuilder</c> only orchestrates the containers (bitcoind, the LND nodes and their startup
/// channels); it stays private to this fixture. Every LND client the tests get is our own
/// <see cref="LndNodeConnection"/> (<c>test/NLightning.Testing.Lnd</c>, LND 0.21.4 protos), built from the gRPC
/// endpoint, <c>tls.cert</c> and <c>admin.macaroon</c> the builder read out of each container (test harness phase 3
/// lane B).
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

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly SharedObjectCache _shared = new();
    private readonly List<LndNodeConnection> _lndNodes = [];
    private LNUnitBuilder? _builder;

    public LightningRegtestNetworkFixture()
    {
        SetupNetwork().GetAwaiter().GetResult();
    }

    public RPCClient Bitcoin =>
        _builder?.BitcoinRpcClient ?? throw new InvalidOperationException("The regtest network is not running");

    /// <summary>
    /// The ZMQ ports of the miner's raw block and raw tx feeds, on <see cref="Bitcoin"/>'s host.
    /// </summary>
    public (int RawBlockPort, int RawTxPort) BitcoinZmqPorts
    {
        get
        {
            var bitcoinConfiguration = _builder?.Configuration.BTCNodes[0]
                                    ?? throw new InvalidOperationException("The regtest network is not running");
            return (ParsePort("-zmqpubrawblock"), ParsePort("-zmqpubrawtx"));

            int ParsePort(string option) =>
                int.Parse(bitcoinConfiguration.Cmd.First(c => c.Contains(option)).Split(':')[2]);
        }
    }

    /// <summary>
    /// The four LND nodes, in <see cref="LndAliases"/> order (all ready once <see cref="SetupNetwork"/> returned).
    /// </summary>
    public IReadOnlyList<LndNodeConnection> LndNodes =>
        _lndNodes.Count > 0
            ? _lndNodes.ToList()
            : throw new InvalidOperationException("The regtest network is not running");

    /// <summary>
    /// The LND node with <paramref name="alias"/> (<c>alice</c>, <c>bob</c>, <c>carol</c> or <c>david</c>).
    /// </summary>
    /// <remarks>
    /// The connection stays the same across container restarts: gRPC reconnects to the same address and the
    /// restarted LND keeps its <c>tls.cert</c>, which the connection pins.
    /// </remarks>
    public LndNodeConnection GetLndNode(string alias) =>
        _lndNodes.FirstOrDefault(n => n.LocalAlias == alias)
     ?? throw new InvalidOperationException($"LND node {alias} is not ready");

    /// <summary>
    /// Restarts the container of the LND node <paramref name="alias"/> (a plain container restart: same container,
    /// network and data). The caller waits for LND to be ready again.
    /// </summary>
    public Task RestartLndAsync(string alias) =>
        (_builder ?? throw new InvalidOperationException("The regtest network is not running")).RestartByAlias(alias);

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

        foreach (var lnd in _lndNodes)
            lnd.Dispose();
        _lndNodes.Clear();

        // Remove containers
        foreach (var name in ContainerNames)
            DockerContainerUtils.RemoveContainerAsync(_client, name).GetAwaiter().GetResult();

        _builder?.Destroy();
        _client.Dispose();
    }

    public async Task SetupNetwork()
    {
        foreach (var name in ContainerNames)
            await DockerContainerUtils.RemoveContainerAsync(_client, name);

        await EnsureLndImageAsync();
        var builder = _builder = new LNUnitBuilder();

        builder.AddBitcoinCoreNode();

        builder.AddPolarLNDNode("alice",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemoteName = "bob"
            }
        ], imageName: LndImageName, tagName: LndImageTag, pullImage: false);
        // alice signals LND's option_simple_close (bits 61/161, "rbf-coop-close"): used only with a peer that signals it
        // too (CooperativeCloseFlowTests' simple-close cases); every other close with her stays legacy
        builder.Configuration.LNDNodes.Single(n => n.Name == "alice").Cmd.Add("--protocol.rbf-coop-close");
        // alice receives spontaneous payments (keysend, lane lh1-l3: KeysendFlowTests); off by default in LND
        builder.Configuration.LNDNodes.Single(n => n.Name == "alice").Cmd.Add("--accept-keysend");

        builder.AddPolarLNDNode("bob",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "alice"
            }
        ], imageName: LndImageName, tagName: LndImageTag, pullImage: false);

        builder.AddPolarLNDNode("carol",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "alice"
            },
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "bob"
            }
        ], imageName: LndImageName, tagName: LndImageTag, pullImage: false);

        // No channels: the ABCD tests connect David to our Carol
        builder.AddPolarLNDNode("david", [], imageName: LndImageName, tagName: LndImageTag, pullImage: false);

        await builder.Build();
        await ConnectLndNodesAsync(builder);
    }

    /// <summary>
    /// Opens our own gRPC connection to each LND node, from the endpoint, certificate and macaroon LNUnit's builder
    /// read out of its container.
    /// </summary>
    private async Task ConnectLndNodesAsync(LNUnitBuilder builder)
    {
        var lnUnitNodes = builder.LNDNodePool?.ReadyNodes.ToList()
                       ?? throw new InvalidOperationException("LNUnit started no LND node");
        foreach (var lnd in _lndNodes)
            lnd.Dispose();
        _lndNodes.Clear();
        foreach (var alias in LndAliases)
        {
            var lnUnitNode = lnUnitNodes.FirstOrDefault(n => n.LocalAlias == alias)
                          ?? throw new InvalidOperationException($"LND node {alias} is not ready");
            var settings = LndSettings.FromBase64(lnUnitNode.Settings.GrpcEndpoint!,
                                                  lnUnitNode.Settings.TlsCertBase64!,
                                                  lnUnitNode.Settings.MacaroonBase64);
            _lndNodes.Add(await LndNodeConnection.ConnectAsync(settings));
        }
    }

    /// <summary>
    /// Builds <see cref="LndImageName"/>:<see cref="LndImageTag"/> from <c>test/Docker/custom_lnd</c> when it is missing
    /// (the Dockerfile's <c>LND_VERSION</c> must equal <see cref="LndImageTag"/>); an existing image is used as is.
    /// </summary>
    private async Task EnsureLndImageAsync()
    {
        var image = $"{LndImageName}:{LndImageTag}";
        try
        {
            await _client.Images.InspectImageAsync(image);
            return;
        }
        catch (DockerImageNotFoundException)
        {
        }

        await _client.CreateDockerImageFromPath("../../../../Docker/custom_lnd", [image]);
    }
}