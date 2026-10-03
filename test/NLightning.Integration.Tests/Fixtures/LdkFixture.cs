namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;
using Ldk;

/// <summary>
/// An ldk-server (LDK Node) regtest node for the interop tests (NL-180), on its own bitcoind, never sharing nodes, names
/// or chain state with the LND, CLN or Eclair fixtures: a run namespace of the Kubernetes harness
/// (<see cref="ClusterLdkBackend"/>, test harness phase 4), the fixture's only backend since NL-866 retired the Docker
/// one. Without <c>NLTG_TEST_BACKEND=cluster</c> the fixture starts nothing and every test that uses it is skipped with
/// the reason (<see cref="UnavailableReason"/>); with it, a missing Kubernetes configuration fails the fixture
/// (<see cref="ConfigurationError"/>, NL-860). Run the suite with <c>scripts/run-cluster.sh --matrix ldk</c>.
/// </summary>
/// <remarks>
/// <para>ldk-server has no tags, releases or official image (NL-555): <see cref="LdkImage"/> is a local image built from
/// <c>test/Docker/ldk_server</c> (a pinned commit on pinned Rust and Debian images; a cold build takes 10-20 min):
/// build it once with <c>docker build -t nltg-ldk-server:dc02b76c test/Docker/ldk_server</c>; the cluster never pulls
/// it (<c>ImagePullPolicy.Never</c>, OrbStack's cluster shares the Docker image store).</para>
/// <para>LDK follows bitcoind over RPC (it polls the tip every few seconds) and funds channels from its own BDK wallet.
/// It is driven through <c>ldk-server-cli</c> in its pod (<see cref="LdkClient"/>), so only its p2p port is reached
/// from outside, at its stable ClusterIP, which survives <see cref="RestartLdkAsync"/>. LDK has an alias
/// (<see cref="LdkAlias"/>) and announces that address (NL-556): LDK Node may then announce channels, so it accepts
/// both our private and our public opens (without an alias it refuses announced ones,
/// <c>force_announced_channel_preference</c>) and opens a public channel on <c>open-channel --announce-channel</c>;
/// its own <c>open-channel</c> without that flag and every private open of ours stay unannounced. A test class skips in
/// its constructor (<see cref="SkipIfUnavailable"/>), so its cleanup never touches a fixture that did not start.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class LdkFixture : IAsyncLifetime
{
    /// <summary>The fixture LDK's node name.</summary>
    public const string LdkContainerName = "nltg-ldk";

    public const string LdkImage = "nltg-ldk-server";
    public const string LdkTag = "dc02b76c";
    public const string ConfigPath = "/data/config.toml";

    /// <summary>The alias LDK announces (an alias is what lets LDK Node announce channels).</summary>
    public const string LdkAlias = "nltg-ldk";

    private readonly ClusterAvailability _availability;
    private readonly ClusterLdkBackend? _backend;

    private readonly SharedObjectCache _shared = new();

    public LdkFixture()
        : this(Environment.GetEnvironmentVariable, ClusterAvailability.KubeConfigurationProbe)
    {
    }

    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">Throws when no Kubernetes configuration can be built.</param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    internal LdkFixture(Func<string, string?> environment, Action kubeConfiguration, Action<string>? skip = null)
    {
        _availability = new ClusterAvailability("the LDK fixture", "NL-866", "scripts/run-cluster.sh --matrix ldk",
                                                environment, kubeConfiguration, skip);
        if (_availability.CanStart)
            _backend = new ClusterLdkBackend();
    }

    /// <summary>Why the fixture does not run in this process (the skip reason of its tests); null on the cluster.</summary>
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <summary>Under <c>NLTG_TEST_BACKEND=cluster</c>, why no Kubernetes configuration could be built (NL-860).</summary>
    public string? ConfigurationError => _availability.ConfigurationError;

    public RegtestBitcoinEndpoint Bitcoin => Backend.Bitcoin;

    public LdkClient Ldk => Backend.Ldk;

    /// <summary>LDK's node id (hex, lower case).</summary>
    public string LdkNodeId { get; private set; } = string.Empty;

    /// <summary>The host this process dials LDK at (LDK's stable ClusterIP).</summary>
    public string LdkHost => Backend.LdkHost;

    /// <summary>LDK's p2p port at <see cref="LdkHost"/> (fixed for the fixture's lifetime).</summary>
    public int LdkHostPort => Backend.LdkPort;

    /// <summary>The <c>pubkey@host:port</c> an in-process node connects to.</summary>
    public string LdkAddress => $"{LdkNodeId}@{LdkHost}:{LdkHostPort}";

    /// <summary>
    /// The host LDK dials to reach a listener of this process (<c>host.orb.internal</c> on OrbStack's cluster, or
    /// <c>NLTG_HOST_ADDRESS</c>); listen on every interface.
    /// </summary>
    public string HostAddressForLdk => Backend.HostAddressForPeers;

    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        SkipIfUnavailable();
        return _shared.GetOrCreateAsync(key, factory);
    }

    /// <summary>
    /// Skips the current test when the fixture does not run in this process (<see cref="UnavailableReason"/>); every
    /// member that needs LDK calls it, and a test class calls it in its constructor.
    /// </summary>
    public void SkipIfUnavailable() => _availability.SkipIfUnavailable();

    public async ValueTask InitializeAsync()
    {
        if (UnavailableReason is not null)
        {
            Console.WriteLine($"[fixture] LDK fixture not started: {UnavailableReason}");
            return;
        }

        _availability.ThrowIfMisconfigured();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Backend.StartAsync(TestContext.Current.CancellationToken);
            var info = await Ldk.GetNodeInfoAsync(CancellationToken.None);
            LdkNodeId = info["node_id"]!.GetValue<string>();
            Console.WriteLine($"[ldk] {LdkAddress}: {info.ToJsonString()}");
            Console.WriteLine($"[fixture] LDK fixture (cluster) ready in {watch.Elapsed.TotalSeconds:F1} s");
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
    /// Writes the last <paramref name="tail"/> lines of LDK's log to <see cref="Console"/> (the test output); a failed
    /// test also gets a full dump of the namespace under <c>TestResults/cluster/</c>. Does nothing when LDK never ran.
    /// </summary>
    public Task DumpLdkLogAsync(int tail = 300) => _backend?.DumpLdkLogAsync(tail) ?? Task.CompletedTask;

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
    public Task RestartLdkAsync(CancellationToken cancellationToken) => Backend.RestartLdkAsync(cancellationToken);

    private ClusterLdkBackend Backend
    {
        get
        {
            SkipIfUnavailable();
            return _backend ?? throw new InvalidOperationException(UnavailableReason ?? ConfigurationError);
        }
    }
}