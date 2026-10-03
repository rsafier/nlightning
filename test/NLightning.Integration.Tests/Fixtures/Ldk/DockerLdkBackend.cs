using Docker.DotNet;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Fixtures.Ldk;

using Docker.Utils;

/// <summary>
/// The Docker backend of <see cref="LdkFixture"/> (the default, unchanged from the fixture before the cluster port): its
/// own bitcoind 31.1 (<see cref="LdkFixture.BitcoinContainerName"/>, <see cref="InteropChainHost"/>) and ldk-server
/// (<see cref="LdkFixture.LdkContainerName"/>) in its own Docker network (<see cref="LdkFixture.NetworkName"/>); LDK's
/// p2p port published on <c>127.0.0.1</c> at a fixed <see cref="PortPoolUtil"/> port that survives
/// <see cref="RestartLdkAsync"/>, and announced there; the peers dial us at
/// <see cref="ClnFixture.HostAddressFromContainers"/>.
/// </summary>
/// <remarks>
/// ldk-server has no tags, releases or official image (NL-555), so the backend builds <see cref="LdkFixture.LdkImage"/>
/// from <c>test/Docker/ldk_server</c> (a pinned commit on pinned Rust and Debian images) when the tag is missing. A cold
/// build takes 10-20 min; <c>scripts/run-interop.sh ldk --build</c> prebuilds it.
/// </remarks>
public sealed class DockerLdkBackend : ILdkBackend
{
    private const int P2PPort = 9735;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_buildTimeout = TimeSpan.FromMinutes(40);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly InteropChainHost _chain;

    private LdkClient? _ldk;
    private int _p2pHostPort;

    public DockerLdkBackend()
    {
        _chain = new InteropChainHost(_client, LdkFixture.NetworkName, LdkFixture.BitcoinContainerName);
    }

    public TestBackendKind Kind => TestBackendKind.Docker;

    public RegtestBitcoinEndpoint Bitcoin => _chain.Bitcoin;

    public LdkClient Ldk => _ldk ?? throw new InvalidOperationException("The LDK fixture is not running");

    public string LdkHost => "127.0.0.1";

    /// <summary>LDK's p2p port on <c>127.0.0.1</c> (fixed for the fixture's lifetime).</summary>
    public int LdkPort => _p2pHostPort;

    public string HostAddressForPeers => ClnFixture.HostAddressFromContainers;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureLdkImageAsync();
        await DockerContainerUtils.RemoveContainerAsync(_client, LdkFixture.LdkContainerName);
        await _chain.StartAsync();

        _p2pHostPort = await PortPoolUtil.GetAvailablePortAsync();
        await _chain.StartContainerAsync($"{LdkFixture.LdkImage}:{LdkFixture.LdkTag}", LdkFixture.LdkContainerName, [],
                                         [LdkFixture.ConfigPath], [P2PPort],
                                         new Dictionary<int, int> { [P2PPort] = _p2pHostPort },
                                         new Dictionary<string, string> { [LdkFixture.ConfigPath] = BuildConfig() });
        _ldk = new LdkClient(_client, LdkFixture.LdkContainerName, LdkFixture.ConfigPath);
        await WaitReadyAsync();
    }

    /// <summary>
    /// Restarts the LDK container (its <c>/data</c> is kept) on the same host port and waits until it is at the tip.
    /// </summary>
    public async Task RestartLdkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _chain.RestartContainerAsync(LdkFixture.LdkContainerName);
        await WaitReadyAsync();
    }

    public Task DumpLdkLogAsync(int tail) =>
        DockerDiagnostics.DumpContainerLogsAsync([LdkFixture.LdkContainerName], tail);

    public async ValueTask DisposeAsync()
    {
        await DockerContainerUtils.RemoveContainerAsync(_client, LdkFixture.LdkContainerName);
        await _chain.RemoveAsync();
        if (_p2pHostPort != 0)
            PortPoolUtil.ReleasePort(_p2pHostPort);
        _client.Dispose();
    }

    /// <summary>The <c>config.toml</c> copied into the container (pinned against the cluster's by a unit test).</summary>
    public static string BuildConfig(int p2pHostPort) =>
        $"""
         [node]
         network = "regtest"
         listening_addresses = ["0.0.0.0:{P2PPort}"]
         announcement_addresses = ["127.0.0.1:{p2pHostPort}"]
         alias = "{LdkFixture.LdkAlias}"

         [storage.disk]
         dir_path = "/data/ldk"

         [log]
         level = "Info"
         log_to_file = false

         [bitcoind]
         rpc_address = "{LdkFixture.BitcoinContainerName}:{InteropChainHost.RpcPort}"
         rpc_user = "{InteropChainHost.RpcUser}"
         rpc_password = "{InteropChainHost.RpcPassword}"
         """;

    private string BuildConfig() => BuildConfig(_p2pHostPort);

    private async Task WaitReadyAsync()
    {
        await DockerContainerUtils.WaitUntilReadyAsync(LdkFixture.LdkContainerName, async ct =>
        {
            var height = await Ldk.GetBlockHeightAsync(ct);
            var tip = await _chain.GetTipAsync(ct);
            if (height != tip)
                throw new InvalidOperationException($"LDK at {height}, tip {tip}");
        }, s_readyTimeout);
    }

    private async Task EnsureLdkImageAsync()
    {
        if (await InteropChainHost.ImageExistsAsync(_client, $"{LdkFixture.LdkImage}:{LdkFixture.LdkTag}"))
            return;

        var dockerfileDir = EclairFixture.FindDockerDirectory("ldk_server");
        await EclairFixture.BuildImageAsync(_client, dockerfileDir, $"{LdkFixture.LdkImage}:{LdkFixture.LdkTag}",
                                            s_buildTimeout);
    }
}