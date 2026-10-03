namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;
using Ldk;

/// <summary>
/// An ldk-server (LDK Node) regtest node for the interop tests (NL-180), on its own bitcoind, never sharing containers,
/// names or chain state with the LND, CLN or Eclair fixtures. Where they run is <see cref="TestBackend"/>'s
/// (<c>NLTG_TEST_BACKEND</c>): Docker by default (<see cref="DockerLdkBackend"/>: its own Docker network,
/// <see cref="InteropChainHost"/>, LDK's p2p port published on <c>127.0.0.1</c>), or a run namespace of the Kubernetes
/// harness (<see cref="ClusterLdkBackend"/>, test harness phase 4). The tests see the same members either way.
/// </summary>
/// <remarks>
/// <para>ldk-server has no tags, releases or official image (NL-555): the Docker backend builds <see cref="LdkImage"/>
/// from <c>test/Docker/ldk_server</c> (a pinned commit on pinned Rust and Debian images) when the tag is missing (a
/// cold build takes 10-20 min; <c>scripts/run-interop.sh ldk --build</c> prebuilds it), and the cluster backend runs
/// that local tag (never pulled).</para>
/// <para>LDK follows bitcoind over RPC (it polls the tip every few seconds) and funds channels from its own BDK wallet.
/// It is driven through <c>ldk-server-cli</c> in its container or pod (<see cref="LdkClient"/>), so only its p2p port
/// is reached from outside, at an address that survives <see cref="RestartLdkAsync"/> (a fixed host port on Docker,
/// its stable ClusterIP on the cluster). LDK has an alias (<see cref="LdkAlias"/>) and announces that address (NL-556):
/// LDK Node may then announce channels, so it accepts both our private and our public opens (without an alias it
/// refuses announced ones, <c>force_announced_channel_preference</c>) and opens a public channel on
/// <c>open-channel --announce-channel</c>; its own <c>open-channel</c> without that flag and every private open of ours
/// stay unannounced.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class LdkFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-ldk-net";
    public const string BitcoinContainerName = "nltg-ldk-bitcoind";

    /// <summary>The fixture LDK's container (Docker) or node (cluster) name.</summary>
    public const string LdkContainerName = "nltg-ldk";

    public const string LdkImage = "nltg-ldk-server";
    public const string LdkTag = "dc02b76c";
    public const string ConfigPath = "/data/config.toml";

    /// <summary>The alias LDK announces (an alias is what lets LDK Node announce channels).</summary>
    public const string LdkAlias = "nltg-ldk";

    private readonly ILdkBackend _backend =
        TestBackend.Current == TestBackendKind.Cluster ? new ClusterLdkBackend() : new DockerLdkBackend();

    private readonly SharedObjectCache _shared = new();

    /// <summary>Where the fixture runs (<see cref="TestBackend.Current"/> when xunit created it).</summary>
    public TestBackendKind Backend => _backend.Kind;

    public RegtestBitcoinEndpoint Bitcoin => _backend.Bitcoin;

    public LdkClient Ldk => _backend.Ldk;

    /// <summary>LDK's node id (hex, lower case).</summary>
    public string LdkNodeId { get; private set; } = string.Empty;

    /// <summary>
    /// The host this process dials LDK at (<c>127.0.0.1</c> for Docker, LDK's stable ClusterIP for the cluster).
    /// </summary>
    public string LdkHost => _backend.LdkHost;

    /// <summary>LDK's p2p port at <see cref="LdkHost"/> (fixed for the fixture's lifetime).</summary>
    public int LdkHostPort => _backend.LdkPort;

    /// <summary>The <c>pubkey@host:port</c> an in-process node connects to.</summary>
    public string LdkAddress => $"{LdkNodeId}@{LdkHost}:{LdkHostPort}";

    /// <summary>
    /// The host LDK dials to reach a listener of this process (<see cref="ClnFixture.HostAddressFromContainers"/> for
    /// Docker, <c>host.orb.internal</c> on OrbStack's cluster); listen on every interface.
    /// </summary>
    public string HostAddressForLdk => _backend.HostAddressForPeers;

    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _backend.StartAsync(TestContext.Current.CancellationToken);
            var info = await Ldk.GetNodeInfoAsync(CancellationToken.None);
            LdkNodeId = info["node_id"]!.GetValue<string>();
            Console.WriteLine($"[ldk] {LdkAddress}: {info.ToJsonString()}");
            // One comparable line per backend (test harness: fixture start, Docker against the cluster)
            Console.WriteLine($"[fixture] LDK fixture ({Backend}) ready in {watch.Elapsed.TotalSeconds:F1} s");
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
        await _backend.DisposeAsync();
    }

    /// <summary>
    /// Writes the last <paramref name="tail"/> lines of LDK's log to <see cref="Console"/> (the test output). On the
    /// cluster backend a failed test also gets a full dump of the namespace under <c>TestResults/cluster/</c>.
    /// </summary>
    public Task DumpLdkLogAsync(int tail = 300) => _backend.DumpLdkLogAsync(tail);

    /// <summary>bitcoind's tip height.</summary>
    public async Task<uint> GetTipAsync(CancellationToken cancellationToken) =>
        (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);

    /// <summary>Mines <paramref name="blocks"/> blocks to the miner wallet.</summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
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
            var tip = await GetTipAsync(cancellationToken);
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
        await MineAsync(blocks, cancellationToken);
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
        await Bitcoin.Rpc.SendToAddressAsync(NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                                             NBitcoin.Money.Satoshis((long)amount.Satoshi),
                                             cancellationToken: cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken);
        await Poll.UntilAsync(async () => await SpendableSatAsync(cancellationToken) > before,
                              TimeSpan.FromSeconds(90), "LDK sees its deposit confirmed", cancellationToken,
                              TimeSpan.FromSeconds(1));
    }

    public async Task<long> SpendableSatAsync(CancellationToken cancellationToken) =>
        (await Ldk.GetBalancesAsync(cancellationToken))["spendable_onchain_balance_sats"]?.GetValue<long>() ?? 0;

    /// <summary>
    /// Restarts LDK (its data is kept) at the same address and waits until it is at the tip.
    /// </summary>
    public Task RestartLdkAsync(CancellationToken cancellationToken) => _backend.RestartLdkAsync(cancellationToken);
}