namespace NLightning.Integration.Tests.Fixtures;

using Cln;
using Docker.Utils;

/// <summary>
/// A Core Lightning (CLN) regtest node for the interop tests, on its own bitcoind, never sharing containers, names or
/// chain state with the LND <c>regtest</c> collection. Where they run is <see cref="TestBackend"/>'s
/// (<c>NLTG_TEST_BACKEND</c>): Docker by default (<see cref="DockerClnBackend"/>: its own Docker network, ports
/// published on <c>127.0.0.1</c>), or a run namespace of the Kubernetes harness (<see cref="ClusterClnBackend"/>, test
/// harness phase 2). The tests see the same members either way.
/// </summary>
/// <remarks>
/// CLN (the official <c>elementsproject/lightningd</c> image, <see cref="ClnTag"/>) runs with
/// <c>--developer --dev-bitcoind-poll=1</c> so it sees a new block within a second (the default poll is 30 s), and
/// with <c>--ignore-fee-limits=false</c>: on testnet/regtest CLN ignores its feerate limits by default (only its
/// mainnet config checks them), which would make any feerate we send look fine.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ClnFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-cln-net";
    public const string BitcoinContainerName = "nltg-cln-bitcoind";

    /// <summary>The fixture CLN's container (Docker) or node (cluster) name, also its alias.</summary>
    public const string ClnContainerName = "nltg-cln";

    /// <summary>
    /// The CLN release the interop tests were written against (pinned so a new release is a deliberate change; the
    /// cluster backend's image is the same release by digest, <c>ImageVersions.Cln</c>).
    /// </summary>
    public const string ClnImage = "elementsproject/lightningd";

    public const string ClnTag = "v26.06.8";

    /// <summary>
    /// How a Docker container reaches a port the test process listens on (OrbStack and Docker Desktop resolve it to
    /// the host, on Linux the containers get a <c>host-gateway</c> alias; the listener must bind a non-loopback address).
    /// The backend-neutral member is <see cref="HostAddressForCln"/>.
    /// </summary>
    public const string HostAddressFromContainers = "host.docker.internal";

    /// <summary>The bitcoind RPC credentials and port of the Docker backend (the cluster's chain uses the same).</summary>
    public const string RpcUser = "nltg";

    public const string RpcPassword = "nltg";

    public const int RpcPort = 18443;

    /// <summary>CLN's p2p port in its container or pod.</summary>
    public const int ClnP2PPort = 9735;

    private readonly IClnBackend _backend =
        TestBackend.Current == TestBackendKind.Cluster ? new ClusterClnBackend() : new DockerClnBackend();

    private readonly SharedObjectCache _shared = new();

    /// <summary>Where the fixture runs (<see cref="TestBackend.Current"/> when xunit created it).</summary>
    public TestBackendKind Backend => _backend.Kind;

    /// <summary>
    /// The fixture's bitcoind, for <see cref="NLightningTestNode.CreateAsync(RegtestBitcoinEndpoint, string, TestNodeDatabase?, Action{Domain.Node.Options.NodeOptions}?)"/>.
    /// </summary>
    public RegtestBitcoinEndpoint Bitcoin => _backend.Bitcoin;

    public ClnClient Cln => _backend.Cln;

    /// <summary>
    /// The host this process dials CLN at (<c>127.0.0.1</c> for Docker, CLN's pod IP for the cluster).
    /// </summary>
    public string ClnHost => _backend.ClnHost;

    /// <summary>
    /// CLN's p2p port at <see cref="ClnHost"/>.
    /// </summary>
    public int ClnHostPort => _backend.ClnPort;

    /// <summary>
    /// CLN's node id (hex, lower case).
    /// </summary>
    public string ClnNodeId => _backend.ClnNodeId;

    /// <summary>
    /// The <c>pubkey@host:port</c> an in-process node connects to.
    /// </summary>
    public string ClnAddress => $"{ClnNodeId}@{ClnHost}:{ClnHostPort}";

    /// <summary>
    /// The host CLN dials to reach a listener of this process (<see cref="HostAddressFromContainers"/> for Docker,
    /// <c>host.orb.internal</c> on OrbStack's cluster); listen on every interface.
    /// </summary>
    public string HostAddressForCln => _backend.HostAddressForPeers;

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/> (see
    /// <see cref="LightningRegtestNetworkFixture.GetOrCreateAsync{T}"/>). Disposed with the fixture.
    /// </summary>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _backend.StartAsync(TestContext.Current.CancellationToken);
            await WaitAllAtTipAsync([], CancellationToken.None);
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
    /// Starts another CLN on the fixture's bitcoind (<paramref name="spec"/>); disposing it removes it.
    /// </summary>
    public Task<ExtraClnNode> StartClnAsync(ClnNodeSpec spec, CancellationToken cancellationToken) =>
        _backend.StartClnAsync(spec, cancellationToken);

    /// <summary>
    /// Writes the last <paramref name="tail"/> lines of CLN's log to <see cref="Console"/> (the test output). On the
    /// cluster backend a failed test also gets a full dump of the namespace under <c>TestResults/cluster/</c>.
    /// </summary>
    public Task DumpClnLogAsync(int tail = 300) => _backend.DumpClnLogAsync(tail);

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks to the bitcoind wallet.
    /// </summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Waits until CLN and every node in <paramref name="nodes"/> have processed bitcoind's tip.
    /// </summary>
    /// <returns>The tip.</returns>
    public async Task<uint> WaitAllAtTipAsync(IEnumerable<NLightningTestNode> nodes,
                                              CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var tip = (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);
            var clnHeight = (uint)(await Cln.GetInfoAsync(cancellationToken))["blockheight"]!.GetValue<long>();
            var ours = nodeList.Select(n => (n.Name,
                                             Height: n.IsRunning ? n.BlockchainMonitor.LastProcessedBlockHeight : 0))
                               .ToList();
            if (clnHeight == tip && ours.All(o => o.Height == tip))
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Not at the tip {tip} in time: cln {clnHeight}, "
                  + string.Join(", ", ours.Select(o => $"{o.Name} {o.Height}")));

            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>
    /// Sends <paramref name="amount"/> from bitcoind to a new CLN wallet address, mines 6 blocks and waits until CLN
    /// lists the output as confirmed (so a following <c>fundchannel</c> can spend it).
    /// </summary>
    public async Task FundClnWalletAsync(Domain.Money.LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                         CancellationToken cancellationToken)
    {
        var address = (await Cln.CallAsync("newaddr", cancellationToken, ("addresstype", "bech32")))["bech32"]!
           .GetValue<string>();
        var txId = await Bitcoin.Rpc.SendToAddressAsync(
                       NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                       NBitcoin.Money.Satoshis((long)amount.Satoshi), cancellationToken: cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await Cln.CallAsync("listfunds", cancellationToken))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == txId.ToString()
                                 && o["status"]?.GetValue<string>() == "confirmed");
        }, TimeSpan.FromSeconds(60), "CLN sees its deposit confirmed", cancellationToken);
    }

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks, then waits until CLN and <paramref name="nodes"/> are at the tip.
    /// </summary>
    public async Task<uint> MineAndWaitAsync(int blocks, IEnumerable<NLightningTestNode> nodes,
                                             CancellationToken cancellationToken)
    {
        await MineAsync(blocks, cancellationToken);
        return await WaitAllAtTipAsync(nodes, cancellationToken);
    }
}