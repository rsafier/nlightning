using System.Net;
using Docker.DotNet;
using LNUnit.Setup;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;
using ServiceStack;

namespace NLightning.Integration.Tests.Fixtures.Lnd;

using Docker.Utils;

/// <summary>
/// The Docker backend of <see cref="LightningRegtestNetworkFixture"/> (the default, unchanged from the fixture before
/// the cluster port): LNUnit's <c>LNUnitBuilder</c> starts bitcoind (<c>miner</c>) and the four LND containers with
/// their startup channels; every LND client the tests get is our own <see cref="LndNodeConnection"/>
/// (<c>test/NLightning.Testing.Lnd</c>, LND 0.21.4 protos), built from the gRPC endpoint, <c>tls.cert</c> and
/// <c>admin.macaroon</c> the builder read out of each container (test harness phase 3 lane B).
/// </summary>
/// <remarks>
/// The containers (<see cref="LightningRegtestNetworkFixture.ContainerNames"/>) are force-removed before the network
/// starts and when it is disposed. Our nodes dial LND at its container address (resolved from the gRPC endpoint), and
/// LND dials us at <see cref="HostAddressForPeers"/>.
/// </remarks>
public sealed class DockerLndBackend : ILndNetworkBackend
{
    /// <summary>LND's p2p port in its container.</summary>
    private const int LndP2PPort = 9735;

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly List<LndNodeConnection> _lndNodes = [];
    private LNUnitBuilder? _builder;
    private int _disposed;

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

    /// <summary>
    /// <c>HOST_ADDRESS</c> when set, otherwise <c>host.docker.internal</c> (OrbStack and Docker Desktop resolve it to
    /// the host).
    /// </summary>
    public string HostAddressForPeers =>
        Environment.GetEnvironmentVariable("HOST_ADDRESS") is { Length: > 0 } configured
            ? configured
            : "host.docker.internal";

    public LndNodeConnection GetLndNode(string alias) =>
        _lndNodes.FirstOrDefault(n => n.LocalAlias == alias)
     ?? throw new InvalidOperationException($"LND node {alias} is not ready");

    /// <summary>
    /// The container's address and p2p port (<c>IP:9735</c>), resolved from the gRPC endpoint's host. A plain container
    /// restart keeps the address in practice (<c>ReestablishFlowTests</c> holds the lower free addresses during alice's
    /// restart, NL-262).
    /// </summary>
    public async Task<string> GetLndPeerEndpointAsync(LndNodeConnection lnd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lnd);
        var host = lnd.Host.SplitOnFirst("//")[1].SplitOnFirst(":")[0];
        var address = (await Dns.GetHostAddressesAsync(host, cancellationToken)).First();
        return new IPEndPoint(address, LndP2PPort).ToString();
    }

    /// <summary>
    /// A plain container restart (LNUnit's <c>RestartByAlias</c> with its defaults: same container, network and
    /// data); the caller waits for LND to be ready again.
    /// </summary>
    public Task RestartLndAsync(string alias) =>
        (_builder ?? throw new InvalidOperationException("The regtest network is not running")).RestartByAlias(alias);

    public Task DumpLndLogsAsync(IEnumerable<string> aliases, int tail) =>
        DockerDiagnostics.DumpContainerLogsAsync(aliases, tail);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in LightningRegtestNetworkFixture.ContainerNames)
            await DockerContainerUtils.RemoveContainerAsync(_client, name);

        await EnsureLndImageAsync();
        var builder = _builder = new LNUnitBuilder();

        builder.AddBitcoinCoreNode();

        const string imageName = LightningRegtestNetworkFixture.LndImageName;
        const string tagName = LightningRegtestNetworkFixture.LndImageTag;
        builder.AddPolarLNDNode("alice",
        [
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemoteName = "bob"
            }
        ], imageName: imageName, tagName: tagName, pullImage: false);
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
        ], imageName: imageName, tagName: tagName, pullImage: false);

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
        ], imageName: imageName, tagName: tagName, pullImage: false);

        // No channels: the ABCD tests connect David to our Carol
        builder.AddPolarLNDNode("david", [], imageName: imageName, tagName: tagName, pullImage: false);

        await builder.Build();
        await ConnectLndNodesAsync(builder);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        foreach (var lnd in _lndNodes)
            lnd.Dispose();
        _lndNodes.Clear();

        // Remove the containers, unless the start never got to the builder (it removed them before anything else, and a
        // backend that never started must not remove another process's containers)
        if (_builder is not null)
        {
            foreach (var name in LightningRegtestNetworkFixture.ContainerNames)
                DockerContainerUtils.RemoveContainerAsync(_client, name).GetAwaiter().GetResult();
            _builder.Destroy();
        }

        _client.Dispose();
        return ValueTask.CompletedTask;
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
    /// from <c>test/Docker/custom_lnd</c> when it is missing (the Dockerfile's <c>LND_VERSION</c> must equal the tag); an
    /// existing image is used as is.
    /// </summary>
    private async Task EnsureLndImageAsync()
    {
        var image = $"{LightningRegtestNetworkFixture.LndImageName}:{LightningRegtestNetworkFixture.LndImageTag}";
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