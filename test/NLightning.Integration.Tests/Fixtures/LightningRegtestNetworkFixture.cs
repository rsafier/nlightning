using Docker.DotNet;
using LNUnit.LND;
using LNUnit.Setup;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// The shared regtest network of the <c>regtest</c> collection: bitcoind (<c>miner</c>) and four LND nodes.
/// <c>alice</c>, <c>bob</c> and <c>carol</c> get LND-LND channels at startup; <c>david</c> has none, so the ABCD
/// tests can give him exactly the channels they need.
/// </summary>
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

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly SharedObjectCache _shared = new();

    public LightningRegtestNetworkFixture()
    {
        SetupNetwork().GetAwaiter().GetResult();
    }

    public LNUnitBuilder? Builder { get; private set; }

    public RPCClient Bitcoin =>
        Builder?.BitcoinRpcClient ?? throw new InvalidOperationException("The regtest network is not running");

    /// <summary>
    /// The LND nodes that are ready (all four once <see cref="SetupNetwork"/> returned).
    /// </summary>
    public IReadOnlyList<LNDNodeConnection> LndNodes =>
        Builder?.LNDNodePool?.ReadyNodes.ToList()
     ?? throw new InvalidOperationException("The regtest network is not running");

    /// <summary>
    /// The LND node with <paramref name="alias"/> (<c>alice</c>, <c>bob</c>, <c>carol</c> or <c>david</c>).
    /// </summary>
    public LNDNodeConnection GetLndNode(string alias) =>
        LndNodes.FirstOrDefault(n => n.LocalAlias == alias)
     ?? throw new InvalidOperationException($"LND node {alias} is not ready");

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

        // Remove containers
        foreach (var name in ContainerNames)
            DockerContainerUtils.RemoveContainerAsync(_client, name).GetAwaiter().GetResult();

        Builder?.Destroy();
        _client.Dispose();
    }

    public async Task SetupNetwork()
    {
        foreach (var name in ContainerNames)
            await DockerContainerUtils.RemoveContainerAsync(_client, name);

        await _client.CreateDockerImageFromPath("../../../../Docker/custom_lnd", ["custom_lnd", "custom_lnd:latest"]);
        Builder = new LNUnitBuilder();

        Builder.AddBitcoinCoreNode();

        Builder.AddPolarLNDNode("alice",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemoteName = "bob"
            }
        ], imageName: "custom_lnd", tagName: "latest", pullImage: false);
        // alice signals LND's option_simple_close (bits 61/161, "rbf-coop-close"): used only with a peer that signals it
        // too (CooperativeCloseFlowTests' simple-close cases); every other close with her stays legacy
        Builder.Configuration.LNDNodes.Single(n => n.Name == "alice").Cmd.Add("--protocol.rbf-coop-close");

        Builder.AddPolarLNDNode("bob",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "alice"
            }
        ], imageName: "custom_lnd", tagName: "latest", pullImage: false);

        Builder.AddPolarLNDNode("carol",
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
        ], imageName: "custom_lnd", tagName: "latest", pullImage: false);

        // No channels: the ABCD tests connect David to our Carol
        Builder.AddPolarLNDNode("david", [], imageName: "custom_lnd", tagName: "latest", pullImage: false);

        await Builder.Build();
    }
}