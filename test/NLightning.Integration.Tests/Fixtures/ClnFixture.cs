namespace NLightning.Integration.Tests.Fixtures;

using Cln;
using Docker.Utils;

/// <summary>
/// A Core Lightning (CLN) regtest node for the interop tests, on its own bitcoind, never sharing nodes, names or chain
/// state with the LND <c>regtest</c> collection: a run namespace of the Kubernetes harness
/// (<see cref="ClusterClnBackend"/>, test harness phase 2), the fixture's only backend since NL-866 retired the Docker
/// one. Without <c>NLTG_TEST_BACKEND=cluster</c> the fixture starts nothing and every test that uses it is skipped with
/// the reason (<see cref="UnavailableReason"/>); with it, a missing Kubernetes configuration fails the fixture
/// (<see cref="ConfigurationError"/>, NL-860). Run the suite with <c>scripts/run-cluster.sh --matrix cln</c>.
/// </summary>
/// <remarks>
/// CLN (the official <c>elementsproject/lightningd</c> image, <see cref="ClnTag"/>) runs with
/// <c>--developer --dev-bitcoind-poll=1</c> so it sees a new block within a second (the default poll is 30 s), and
/// with <c>--ignore-fee-limits=false</c>: on testnet/regtest CLN ignores its feerate limits by default (only its
/// mainnet config checks them), which would make any feerate we send look fine. A test class skips in its constructor
/// (<see cref="SkipIfUnavailable"/>), so its cleanup never touches a fixture that did not start.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ClnFixture : IAsyncLifetime
{
    /// <summary>The fixture CLN's node name, also its alias.</summary>
    public const string ClnContainerName = "nltg-cln";

    /// <summary>
    /// The CLN release the interop tests were written against (pinned so a new release is a deliberate change; the
    /// cluster runs the same release by digest, <c>ImageVersions.Cln</c>, and the Tor suite's container this tag).
    /// </summary>
    public const string ClnImage = "elementsproject/lightningd";

    public const string ClnTag = "v26.06.8";

    /// <summary>CLN's p2p port in its pod.</summary>
    public const int ClnP2PPort = 9735;

    private readonly ClusterAvailability _availability;
    private readonly ClusterClnBackend? _backend;
    private readonly SharedObjectCache _shared = new();

    public ClnFixture()
        : this(Environment.GetEnvironmentVariable, ClusterAvailability.KubeConfigurationProbe)
    {
    }

    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">Throws when no Kubernetes configuration can be built.</param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    internal ClnFixture(Func<string, string?> environment, Action kubeConfiguration, Action<string>? skip = null)
    {
        _availability = new ClusterAvailability("the CLN fixture", "NL-866", "scripts/run-cluster.sh --matrix cln",
                                                environment, kubeConfiguration, skip);
        if (_availability.CanStart)
            _backend = new ClusterClnBackend();
    }

    /// <summary>Why the fixture does not run in this process (the skip reason of its tests); null on the cluster.</summary>
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <summary>Under <c>NLTG_TEST_BACKEND=cluster</c>, why no Kubernetes configuration could be built (NL-860).</summary>
    public string? ConfigurationError => _availability.ConfigurationError;

    /// <summary>
    /// The fixture's bitcoind, for <see cref="NLightningTestNode.CreateAsync(RegtestBitcoinEndpoint, string, TestNodeDatabase?, Action{Domain.Node.Options.NodeOptions}?)"/>.
    /// </summary>
    public RegtestBitcoinEndpoint Bitcoin => Backend.Bitcoin;

    public ClnClient Cln => Backend.Cln;

    /// <summary>The host this process dials CLN at (CLN's pod IP).</summary>
    public string ClnHost => Backend.ClnHost;

    /// <summary>
    /// CLN's p2p port at <see cref="ClnHost"/>.
    /// </summary>
    public int ClnHostPort => Backend.ClnPort;

    /// <summary>
    /// CLN's node id (hex, lower case).
    /// </summary>
    public string ClnNodeId => Backend.ClnNodeId;

    /// <summary>
    /// The <c>pubkey@host:port</c> an in-process node connects to.
    /// </summary>
    public string ClnAddress => $"{ClnNodeId}@{ClnHost}:{ClnHostPort}";

    /// <summary>
    /// The host CLN dials to reach a listener of this process (<c>host.orb.internal</c> on OrbStack's cluster, or
    /// <c>NLTG_HOST_ADDRESS</c>); listen on every interface.
    /// </summary>
    public string HostAddressForCln => Backend.HostAddressForPeers;

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/> (see
    /// <see cref="LightningRegtestNetworkFixture.GetOrCreateAsync{T}"/>). Disposed with the fixture.
    /// </summary>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        SkipIfUnavailable();
        return _shared.GetOrCreateAsync(key, factory);
    }

    /// <summary>
    /// Skips the current test when the fixture does not run in this process (<see cref="UnavailableReason"/>); every
    /// member that needs CLN calls it, and a test class calls it in its constructor.
    /// </summary>
    public void SkipIfUnavailable() => _availability.SkipIfUnavailable();

    public async ValueTask InitializeAsync()
    {
        if (UnavailableReason is not null)
        {
            Console.WriteLine($"[fixture] CLN fixture not started: {UnavailableReason}");
            return;
        }

        _availability.ThrowIfMisconfigured();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Backend.StartAsync(TestContext.Current.CancellationToken);
            await WaitAllAtTipAsync([], CancellationToken.None);
            Console.WriteLine($"[fixture] CLN fixture (cluster) ready in {watch.Elapsed.TotalSeconds:F1} s");
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
        if (_backend is not null)
            await _backend.DisposeAsync();
    }

    /// <summary>
    /// Starts another CLN on the fixture's bitcoind (<paramref name="spec"/>); disposing it removes it.
    /// </summary>
    public Task<ExtraClnNode> StartClnAsync(ClnNodeSpec spec, CancellationToken cancellationToken) =>
        Backend.StartClnAsync(spec, cancellationToken);

    /// <summary>
    /// Writes the last <paramref name="tail"/> lines of CLN's log to <see cref="Console"/> (the test output); a failed
    /// test also gets a full dump of the namespace under <c>TestResults/cluster/</c>. Does nothing when CLN never ran.
    /// </summary>
    public Task DumpClnLogAsync(int tail = 300) => _backend?.DumpClnLogAsync(tail) ?? Task.CompletedTask;

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

    private ClusterClnBackend Backend
    {
        get
        {
            SkipIfUnavailable();
            return _backend ?? throw new InvalidOperationException(UnavailableReason ?? ConfigurationError);
        }
    }
}