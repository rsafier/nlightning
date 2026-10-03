using Docker.DotNet;
using LNUnit.Setup;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;

namespace NLightning.Integration.Tests.Fixtures.Lnd;

/// <summary>
/// The Docker backend of <see cref="LightningRegtestNetworkFixture"/> (<c>NLTG_TEST_BACKEND=docker</c>, the default):
/// bitcoind (<c>miner</c>) and the four LND containers (<see cref="LightningRegtestNetworkFixture.ContainerNames"/>)
/// with their startup channels, orchestrated by LNUnit's <c>LNUnitBuilder</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only code of the test projects that uses LNUnit (NL-819): the other Docker fixtures pull their images
/// with <see cref="DockerContainerUtils.EnsureImageAsync"/>, and every LND client the tests get is our own
/// <see cref="LndNodeConnection"/> (<c>test/NLightning.Testing.Lnd</c>, LND 0.21.4 protos), built from the gRPC
/// endpoint, <c>tls.cert</c> and <c>admin.macaroon</c> the builder read out of each container (test harness phase 3
/// lane B). The <c>LNUnit</c> package brings <c>lnunit.lnd</c> (its own LND client) along transitively; nothing here
/// uses it.
/// </para>
/// <para>
/// <see cref="RestartLndAsync"/> is a plain container restart (LNUnit's <c>RestartByAlias</c> with its defaults: same
/// container, network and data) and returns once the container runs, not once LND serves every RPC: the caller waits
/// for LND (<c>ReestablishFlowTests</c> polls <c>GetInfo</c> with a deadline, because LNUnit's own readiness wait can
/// block without one).
/// </para>
/// </remarks>
public sealed class DockerLndBackend : ILndNetworkBackend
{
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly List<LndNodeConnection> _lndNodes = [];
    private LNUnitBuilder? _builder;

    public TestBackendKind Kind => TestBackendKind.Docker;

    public RPCClient Bitcoin =>
        _builder?.BitcoinRpcClient ?? throw new InvalidOperationException("The regtest network is not running");

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

    public IReadOnlyList<LndNodeConnection> LndNodes =>
        _lndNodes.Count > 0
            ? _lndNodes.ToList()
            : throw new InvalidOperationException("The regtest network is not running");

    /// <remarks>
    /// The connection stays the same across container restarts: gRPC reconnects to the same address and the
    /// restarted LND keeps its <c>tls.cert</c>, which the connection pins.
    /// </remarks>
    public LndNodeConnection GetLndNode(string alias) =>
        _lndNodes.FirstOrDefault(n => n.LocalAlias == alias)
     ?? throw new InvalidOperationException($"LND node {alias} is not ready");

    public Task RestartLndAsync(string alias) =>
        (_builder ?? throw new InvalidOperationException("The regtest network is not running")).RestartByAlias(alias);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in LightningRegtestNetworkFixture.ContainerNames)
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
        ], imageName: LightningRegtestNetworkFixture.LndImageName, tagName: LightningRegtestNetworkFixture.LndImageTag,
                                pullImage: false);
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
        ], imageName: LightningRegtestNetworkFixture.LndImageName, tagName: LightningRegtestNetworkFixture.LndImageTag,
                                pullImage: false);

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
        ], imageName: LightningRegtestNetworkFixture.LndImageName, tagName: LightningRegtestNetworkFixture.LndImageTag,
                                pullImage: false);

        // No channels: the ABCD tests connect David to our Carol
        builder.AddPolarLNDNode("david", [], imageName: LightningRegtestNetworkFixture.LndImageName,
                                tagName: LightningRegtestNetworkFixture.LndImageTag, pullImage: false);

        await builder.Build();
        await ConnectLndNodesAsync(builder);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lnd in _lndNodes)
            lnd.Dispose();
        _lndNodes.Clear();

        // Remove containers
        foreach (var name in LightningRegtestNetworkFixture.ContainerNames)
            await DockerContainerUtils.RemoveContainerAsync(_client, name);

        // The containers are gone already: LNUnit's Destroy() would only fail on them (the fixture used to call it
        // without awaiting it), so the builder just releases its Docker client and its own LND connection pool
        _builder?.Dispose();
        _client.Dispose();
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
        foreach (var alias in LightningRegtestNetworkFixture.LndAliases)
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
    /// Builds <see cref="LightningRegtestNetworkFixture.LndImageName"/>:<see cref="LightningRegtestNetworkFixture.LndImageTag"/>
    /// from <c>test/Docker/custom_lnd</c> when it is missing (the Dockerfile's <c>LND_VERSION</c> must equal the tag);
    /// an existing image is used as is.
    /// </summary>
    private async Task EnsureLndImageAsync()
    {
        var image = $"{LightningRegtestNetworkFixture.LndImageName}:{LightningRegtestNetworkFixture.LndImageTag}";
        if (await DockerContainerUtils.ImageExistsAsync(_client, image))
            return;

        await _client.CreateDockerImageFromPath("../../../../Docker/custom_lnd", [image]);
    }
}