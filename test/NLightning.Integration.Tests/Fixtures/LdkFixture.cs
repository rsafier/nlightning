using Docker.DotNet;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;

/// <summary>
/// An ldk-server (LDK Node) regtest node for the interop tests (NL-180), on its own bitcoind in its own Docker network
/// (<see cref="InteropChainHost"/>), so it never shares containers, names or chain state with the LND, CLN or Eclair
/// fixtures.
/// </summary>
/// <remarks>
/// <para>ldk-server has no tags, releases or official image (NL-555), so the fixture builds <see cref="LdkImage"/> from
/// <c>test/Docker/ldk_server</c> (a pinned commit on pinned Rust and Debian images) when the tag is missing. A cold
/// build takes 10-20 min; <c>scripts/run-interop.sh ldk --build</c> prebuilds it.</para>
/// <para>LDK follows bitcoind over RPC (it polls the tip every few seconds) and funds channels from its own BDK wallet.
/// It is driven through <c>ldk-server-cli</c> in the container (<see cref="LdkClient"/>), so only its p2p port is
/// published, on a fixed <see cref="PortPoolUtil"/> port that survives <see cref="RestartLdkAsync"/>. LDK has an alias
/// (<see cref="LdkAlias"/>) and announces <c>127.0.0.1:&lt;host port&gt;</c> (NL-556): LDK Node may then announce
/// channels, so it accepts both our private and our public opens (without an alias it refuses announced ones,
/// <c>force_announced_channel_preference</c>) and opens a public channel on <c>open-channel --announce-channel</c>;
/// its own <c>open-channel</c> without that flag and every private open of ours stay unannounced.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class LdkFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-ldk-net";
    public const string BitcoinContainerName = "nltg-ldk-bitcoind";
    public const string LdkContainerName = "nltg-ldk";

    public const string LdkImage = "nltg-ldk-server";
    public const string LdkTag = "dc02b76c";
    public const string ConfigPath = "/data/config.toml";

    /// <summary>The alias LDK announces (an alias is what lets LDK Node announce channels).</summary>
    public const string LdkAlias = "nltg-ldk";

    private const int P2PPort = 9735;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_buildTimeout = TimeSpan.FromMinutes(40);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly SharedObjectCache _shared = new();
    private readonly InteropChainHost _chain;

    private LdkClient? _ldk;
    private int _p2pHostPort;

    public LdkFixture()
    {
        _chain = new InteropChainHost(_client, NetworkName, BitcoinContainerName);
    }

    public RegtestBitcoinEndpoint Bitcoin => _chain.Bitcoin;

    public InteropChainHost Chain => _chain;

    public LdkClient Ldk => _ldk ?? throw new InvalidOperationException("The LDK fixture is not running");

    /// <summary>LDK's node id (hex, lower case).</summary>
    public string LdkNodeId { get; private set; } = string.Empty;

    /// <summary>LDK's p2p port on <c>127.0.0.1</c> (fixed for the fixture's lifetime).</summary>
    public int LdkHostPort => _p2pHostPort;

    /// <summary>The <c>pubkey@127.0.0.1:port</c> an in-process node connects to.</summary>
    public string LdkAddress => $"{LdkNodeId}@127.0.0.1:{_p2pHostPort}";

    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shared.DisposeAll();
        await DockerContainerUtils.RemoveContainerAsync(_client, LdkContainerName);
        await _chain.RemoveAsync();
        if (_p2pHostPort != 0)
            PortPoolUtil.ReleasePort(_p2pHostPort);
        _client.Dispose();
    }

    /// <summary>
    /// Waits until LDK (<c>get-node-info.current_best_block</c>) and every node in <paramref name="nodes"/> have
    /// processed bitcoind's tip. LDK polls bitcoind, so this allows 60 s by default.
    /// </summary>
    public async Task<uint> WaitAllAtTipAsync(IEnumerable<NLightningTestNode> nodes,
                                              CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var tip = await _chain.GetTipAsync(cancellationToken);
            var ldkHeight = await Ldk.GetBlockHeightAsync(cancellationToken);
            var ours = nodeList.Select(n => (n.Name,
                                             Height: n.IsRunning ? n.BlockchainMonitor.LastProcessedBlockHeight : 0))
                               .ToList();
            if (ldkHeight == tip && ours.All(o => o.Height == tip))
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Not at the tip {tip} in time: ldk {ldkHeight}, "
                  + string.Join(", ", ours.Select(o => $"{o.Name} {o.Height}")));

            await Task.Delay(250, cancellationToken);
        }
    }

    public async Task<uint> MineAndWaitAsync(int blocks, IEnumerable<NLightningTestNode> nodes,
                                             CancellationToken cancellationToken)
    {
        await _chain.MineAsync(blocks, cancellationToken);
        return await WaitAllAtTipAsync(nodes, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="amount"/> from the miner to a new LDK address, mines 6 blocks and waits until LDK's
    /// <c>spendable_onchain_balance_sats</c> grew. Fund LDK before we open to it: LDK refuses an inbound anchors channel
    /// it cannot back with its on-chain reserve (the mirror of our NL-379).
    /// </summary>
    public async Task FundLdkWalletAsync(LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                         CancellationToken cancellationToken)
    {
        var before = await SpendableSatAsync(cancellationToken);
        var address = await Ldk.OnchainReceiveAsync(cancellationToken);
        await _chain.SendToAddressAsync(address, (long)amount.Satoshi, cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken);
        await Poll.UntilAsync(async () => await SpendableSatAsync(cancellationToken) > before,
                              TimeSpan.FromSeconds(90), "LDK sees its deposit confirmed", cancellationToken,
                              TimeSpan.FromSeconds(1));
    }

    public async Task<long> SpendableSatAsync(CancellationToken cancellationToken) =>
        (await Ldk.GetBalancesAsync(cancellationToken))["spendable_onchain_balance_sats"]?.GetValue<long>() ?? 0;

    /// <summary>
    /// Restarts the LDK container (its <c>/data</c> is kept) on the same host port and waits until it is at the tip.
    /// </summary>
    public async Task RestartLdkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _chain.RestartContainerAsync(LdkContainerName);
        await WaitReadyAsync();
    }

    private async Task StartAsync()
    {
        await EnsureLdkImageAsync();
        await DockerContainerUtils.RemoveContainerAsync(_client, LdkContainerName);
        await _chain.StartAsync();

        _p2pHostPort = await PortPoolUtil.GetAvailablePortAsync();
        await _chain.StartContainerAsync($"{LdkImage}:{LdkTag}", LdkContainerName, [], [ConfigPath], [P2PPort],
                                         new Dictionary<int, int> { [P2PPort] = _p2pHostPort },
                                         new Dictionary<string, string> { [ConfigPath] = BuildConfig() });
        _ldk = new LdkClient(_client, LdkContainerName, ConfigPath);
        await WaitReadyAsync();
        var info = await _ldk.GetNodeInfoAsync(CancellationToken.None);
        LdkNodeId = info["node_id"]!.GetValue<string>();
        Console.WriteLine($"[ldk] {LdkAddress}: {info.ToJsonString()}");
    }

    private async Task WaitReadyAsync()
    {
        await DockerContainerUtils.WaitUntilReadyAsync(LdkContainerName, async ct =>
        {
            var height = await Ldk.GetBlockHeightAsync(ct);
            var tip = await _chain.GetTipAsync(ct);
            if (height != tip)
                throw new InvalidOperationException($"LDK at {height}, tip {tip}");
        }, s_readyTimeout);
    }

    private string BuildConfig() =>
        $"""
         [node]
         network = "regtest"
         listening_addresses = ["0.0.0.0:{P2PPort}"]
         announcement_addresses = ["127.0.0.1:{_p2pHostPort}"]
         alias = "{LdkAlias}"

         [storage.disk]
         dir_path = "/data/ldk"

         [log]
         level = "Info"
         log_to_file = false

         [bitcoind]
         rpc_address = "{BitcoinContainerName}:{InteropChainHost.RpcPort}"
         rpc_user = "{InteropChainHost.RpcUser}"
         rpc_password = "{InteropChainHost.RpcPassword}"
         """;

    private async Task EnsureLdkImageAsync()
    {
        if (await InteropChainHost.ImageExistsAsync(_client, $"{LdkImage}:{LdkTag}"))
            return;

        var dockerfileDir = EclairFixture.FindDockerDirectory("ldk_server");
        await EclairFixture.BuildImageAsync(_client, dockerfileDir, $"{LdkImage}:{LdkTag}", s_buildTimeout);
    }
}