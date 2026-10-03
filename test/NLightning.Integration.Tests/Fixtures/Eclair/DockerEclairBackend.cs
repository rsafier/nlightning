using Docker.DotNet;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Fixtures.Eclair;

using Docker.Utils;

/// <summary>
/// The Docker backend of <see cref="EclairFixture"/> (the default, the fixture before the cluster port, unchanged): its
/// own bitcoind 31.1 in its own Docker network (<see cref="InteropChainHost"/>) and Eclair
/// (<see cref="EclairFixture.EclairContainerName"/>) built from <c>test/Docker/eclair</c> when the tag is missing, its
/// p2p and API ports published on fixed <c>127.0.0.1</c> ports from <see cref="PortPoolUtil"/> (Docker gives a
/// restarted container new random ones), so its address survives <see cref="RestartEclairAsync"/>.
/// </summary>
public sealed class DockerEclairBackend : IEclairBackend
{
    private const int P2PPort = 9735;
    private const int ApiPort = 8080;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly InteropChainHost _chain;

    private EclairClient? _eclair;
    private int _p2pHostPort;
    private int _apiHostPort;

    public DockerEclairBackend()
    {
        _chain = new InteropChainHost(_client, EclairFixture.NetworkName, EclairFixture.BitcoinContainerName);
    }

    public TestBackendKind Kind => TestBackendKind.Docker;

    public RegtestBitcoinEndpoint Bitcoin => _chain.Bitcoin;

    public EclairClient Eclair => _eclair ?? throw new InvalidOperationException("The Eclair fixture is not running");

    public string EclairNodeId { get; private set; } = string.Empty;

    public string EclairHost => "127.0.0.1";

    public int EclairPort => _p2pHostPort;

    public string HostAddressForPeers => ClnFixture.HostAddressFromContainers;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureEclairImageAsync();
        await DockerContainerUtils.RemoveContainerAsync(_client, EclairFixture.EclairContainerName);
        await _chain.StartAsync();
        await _chain.CreateWalletAsync("eclair");

        _p2pHostPort = await PortPoolUtil.GetAvailablePortAsync();
        _apiHostPort = await PortPoolUtil.GetAvailablePortAsync();
        var (client, nodeId) = await StartEclairContainerAsync(EclairFixture.EclairContainerName, "eclair",
                                                               _p2pHostPort, _apiHostPort);
        _eclair = client;
        EclairNodeId = nodeId;
        Console.WriteLine($"[eclair] {EclairNodeId}@{EclairHost}:{EclairPort}: {(await client.GetInfoAsync(CancellationToken.None))
           .ToJsonString()}");
    }

    public async Task RestartEclairAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _chain.RestartContainerAsync(EclairFixture.EclairContainerName);
        await WaitReadyAsync(EclairFixture.EclairContainerName, Eclair);
    }

    public Task DumpEclairLogAsync(int tail) =>
        DockerDiagnostics.DumpContainerLogsAsync([EclairFixture.EclairContainerName], tail);

    public async ValueTask DisposeAsync()
    {
        _eclair?.Dispose();
        await DockerContainerUtils.RemoveContainerAsync(_client, EclairFixture.EclairContainerName);
        await _chain.RemoveAsync();
        if (_p2pHostPort != 0)
            PortPoolUtil.ReleasePort(_p2pHostPort);
        if (_apiHostPort != 0)
            PortPoolUtil.ReleasePort(_apiHostPort);
        _client.Dispose();
    }

    private async Task<(EclairClient Client, string NodeId)> StartEclairContainerAsync(
        string containerName, string wallet, int p2pHostPort, int apiHostPort)
    {
        // Both host ports fixed: Docker gives a restarted container new random ones
        var ports = await _chain.StartContainerAsync(
                        $"{EclairFixture.EclairImage}:{EclairFixture.EclairTag}", containerName,
                        ["JAVA_OPTS=-Xmx512m -Declair.printToConsole=true"], [],
                        [P2PPort, ApiPort],
                        new Dictionary<int, int> { [P2PPort] = p2pHostPort, [ApiPort] = apiHostPort },
                        new Dictionary<string, string>
                        {
                            ["/data/eclair.conf"] = BuildConfig(containerName, wallet)
                        });
        var client = new EclairClient(ports[ApiPort], EclairFixture.ApiPassword);
        try
        {
            await WaitReadyAsync(containerName, client);
            var nodeId = (await client.GetInfoAsync(CancellationToken.None))["nodeId"]!.GetValue<string>();
            return (client, nodeId);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task WaitReadyAsync(string containerName, EclairClient client)
    {
        await DockerContainerUtils.WaitUntilReadyAsync(containerName, async ct =>
        {
            var height = await client.GetBlockHeightAsync(ct);
            var tip = await _chain.GetTipAsync(ct);
            if (height != tip)
                throw new InvalidOperationException($"Eclair at {height}, tip {tip}");
        }, s_readyTimeout);
    }

    /// <summary>The <c>eclair.conf</c> of the Docker container (pinned by a unit test).</summary>
    internal static string BuildConfig(string containerName, string wallet) =>
        $"""
         eclair.chain = "regtest"
         eclair.server.port = {P2PPort}
         eclair.api.enabled = true
         eclair.api.binding-ip = "0.0.0.0"
         eclair.api.port = {ApiPort}
         eclair.api.password = "{EclairFixture.ApiPassword}"
         eclair.bitcoind.host = "{EclairFixture.BitcoinContainerName}"
         eclair.bitcoind.rpcport = {InteropChainHost.RpcPort}
         eclair.bitcoind.rpcuser = "{InteropChainHost.RpcUser}"
         eclair.bitcoind.rpcpassword = "{InteropChainHost.RpcPassword}"
         eclair.bitcoind.wallet = "{wallet}"
         eclair.bitcoind.zmqblock = "tcp://{EclairFixture.BitcoinContainerName}:{InteropChainHost.ZmqHashBlockPort}"
         eclair.bitcoind.zmqtx = "tcp://{EclairFixture.BitcoinContainerName}:{InteropChainHost.ZmqTxPort}"
         eclair.node-alias = "{containerName}"
         eclair.channel.min-depth-blocks = 6
         """;

    private async Task EnsureEclairImageAsync()
    {
        var reference = $"{EclairFixture.EclairImage}:{EclairFixture.EclairTag}";
        if (await InteropChainHost.ImageExistsAsync(_client, reference))
            return;

        var dockerfileDir = EclairFixture.FindDockerDirectory("eclair");
        await EclairFixture.BuildImageAsync(_client, dockerfileDir, reference, TimeSpan.FromMinutes(15));
    }
}