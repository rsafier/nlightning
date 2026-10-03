namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;
using Eclair;

/// <summary>
/// An Eclair regtest node for the interop tests (NL-180), on its own bitcoind, never sharing nodes, names or chain state
/// with the LND or CLN fixtures: a run namespace of the Kubernetes harness (<see cref="ClusterEclairBackend"/>, test
/// harness phase 4), the fixture's only backend since NL-866 retired the Docker one. Without
/// <c>NLTG_TEST_BACKEND=cluster</c> the fixture starts nothing and every test that uses it is skipped with the reason
/// (<see cref="UnavailableReason"/>); with it, a missing Kubernetes configuration fails the fixture
/// (<see cref="ConfigurationError"/>, NL-860). Run the suites with <c>scripts/run-cluster.sh --matrix eclair,eclair2</c>.
/// </summary>
/// <remarks>
/// <para>ACINQ publishes no pinned multi-arch image of a recent release (NL-553), so <see cref="EclairImage"/> is a local
/// image built from <c>test/Docker/eclair</c> (the v0.14.3 release zip, sha256-checked, on a pinned Temurin 21 JRE):
/// build it once with <c>docker build -t nltg-eclair:0.14.3 test/Docker/eclair</c>; the cluster never pulls it
/// (<c>ImagePullPolicy.Never</c>, OrbStack's cluster shares the Docker image store).</para>
/// <para>Eclair funds channels from the bitcoind wallet <c>eclair</c>, follows blocks over ZMQ <c>hashblock</c> and
/// answers its JSON API (password <see cref="ApiPassword"/>). Its p2p address (<see cref="EclairAddress"/>) survives
/// <see cref="RestartEclairAsync"/>. Eclair runs with its default channel policy, the <c>to_self_delay</c> of
/// <see cref="EclairDefaultToRemoteDelayBlocks"/> it asks of us included (accepted since NL-550). A test class skips in
/// its constructor (<see cref="SkipIfUnavailable"/>), so its cleanup never touches a fixture that did not start.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class EclairFixture : IAsyncLifetime
{
    /// <summary>The fixture Eclair's node name, also its alias.</summary>
    public const string EclairContainerName = "nltg-eclair";

    /// <summary>
    /// The second Eclair of the fixture, configured as a liquidity seller (<see cref="GetSellerAsync"/>, NL-850); it
    /// shares the chain and is removed with the fixture.
    /// </summary>
    public const string SellerContainerName = "nltg-eclair-seller";

    public const string EclairImage = "nltg-eclair";
    public const string EclairTag = "0.14.3";
    public const string ApiPassword = "nltg";

    /// <summary>
    /// Eclair's default <c>eclair.channel.to-remote-delay-blocks</c>: the <c>to_self_delay</c> it asks of us, within
    /// our <c>Node:MaxAcceptedToSelfDelay</c> (2016) since NL-550.
    /// </summary>
    public const int EclairDefaultToRemoteDelayBlocks = 720;

    private readonly ClusterAvailability _availability;
    private readonly ClusterEclairBackend? _backend;

    private readonly SharedObjectCache _shared = new();

    private readonly SemaphoreSlim _sellerGate = new(1, 1);

    private EclairEndpoint? _seller;

    public EclairFixture()
        : this(Environment.GetEnvironmentVariable, ClusterAvailability.KubeConfigurationProbe)
    {
    }

    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">Throws when no Kubernetes configuration can be built.</param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    internal EclairFixture(Func<string, string?> environment, Action kubeConfiguration, Action<string>? skip = null)
    {
        _availability = new ClusterAvailability("the Eclair fixture", "NL-866",
                                                "scripts/run-cluster.sh --matrix eclair,eclair2", environment,
                                                kubeConfiguration, skip);
        if (_availability.CanStart)
            _backend = new ClusterEclairBackend();
    }

    /// <summary>Why the fixture does not run in this process (the skip reason of its tests); null on the cluster.</summary>
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <summary>Under <c>NLTG_TEST_BACKEND=cluster</c>, why no Kubernetes configuration could be built (NL-860).</summary>
    public string? ConfigurationError => _availability.ConfigurationError;

    public RegtestBitcoinEndpoint Bitcoin => Backend.Bitcoin;

    public EclairClient Eclair => Backend.Eclair;

    /// <summary>Eclair's node id (hex, lower case).</summary>
    public string EclairNodeId => Backend.EclairNodeId;

    /// <summary>
    /// The host this process dials Eclair at (Eclair's stable ClusterIP name); fixed for the fixture's lifetime.
    /// </summary>
    public string EclairHost => Backend.EclairHost;

    /// <summary>Eclair's p2p port at <see cref="EclairHost"/> (fixed for the fixture's lifetime).</summary>
    public int EclairHostPort => Backend.EclairPort;

    /// <summary>The <c>pubkey@host:port</c> an in-process node connects to.</summary>
    public string EclairAddress => $"{EclairNodeId}@{EclairHost}:{EclairHostPort}";

    /// <summary>
    /// The host Eclair dials to reach a listener of this process (<c>host.orb.internal</c> on OrbStack's cluster, or
    /// <c>NLTG_HOST_ADDRESS</c>); listen on every interface.
    /// </summary>
    public string HostAddressForEclair => Backend.HostAddressForPeers;

    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        SkipIfUnavailable();
        return _shared.GetOrCreateAsync(key, factory);
    }

    /// <summary>
    /// Skips the current test when the fixture does not run in this process (<see cref="UnavailableReason"/>); every
    /// member that needs Eclair calls it, and a test class calls it in its constructor.
    /// </summary>
    public void SkipIfUnavailable() => _availability.SkipIfUnavailable();

    public async ValueTask InitializeAsync()
    {
        if (UnavailableReason is not null)
        {
            Console.WriteLine($"[fixture] Eclair fixture not started: {UnavailableReason}");
            return;
        }

        _availability.ThrowIfMisconfigured();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Backend.StartAsync(TestContext.Current.CancellationToken);
            await WaitAllAtTipAsync([], CancellationToken.None);
            Console.WriteLine($"[fixture] Eclair fixture (cluster) ready in {watch.Elapsed.TotalSeconds:F1} s");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The fixture's liquidity seller (NL-850): a second Eclair 0.14.3 on the same chain (<see cref="SellerContainerName"/>,
    /// wallet <c>eclair-seller</c>) whose <c>eclair.liquidity-ads</c> sells at <see cref="SellerRates"/>, paid from the
    /// channel balance only (<see cref="SellerConfigLines"/>): a node in the collection's run namespace (<c>emptyDir</c>,
    /// dialed at its pod IP). Started on first
    /// use, then shared by the tests of the collection; <see cref="WaitAllAtTipAsync"/> waits for it too once it runs.
    /// Its wallet is funded with <paramref name="walletSat"/> on first use (Eclair needs confirmed coins to contribute).
    /// </summary>
    public async Task<EclairEndpoint> GetSellerAsync(CancellationToken cancellationToken, long walletSat = 10_000_000)
    {
        await _sellerGate.WaitAsync(cancellationToken);
        try
        {
            if (_seller is not null)
                return _seller;

            var seller = await Backend.StartSellerAsync(SellerRates, cancellationToken);
            Console.WriteLine($"[eclair-seller] {seller.Address}: {(await seller.Client.GetInfoAsync(cancellationToken))
               .ToJsonString()}");
            _seller = seller;
            await FundEclairWalletAsync(LightningMoney.Satoshis((ulong)walletSat), [], cancellationToken, seller.Client);
            return seller;
        }
        finally
        {
            _sellerGate.Release();
        }
    }

    /// <summary>
    /// Writes the last <paramref name="tail"/> lines of the seller Eclair's log (<see cref="GetSellerAsync"/>) to
    /// <see cref="Console"/>; nothing when it never started.
    /// </summary>
    public Task DumpSellerLogAsync(int tail = 400) => _backend?.DumpSellerLogAsync(tail) ?? Task.CompletedTask;

    /// <summary>
    /// The rate the seller Eclair (<see cref="GetSellerAsync"/>) sells at: 10,000 to 5,000,000 sat, a funding weight
    /// of 400, 500 sat + 100 basis points, and 1,000 sat more for a new channel.
    /// </summary>
    public static readonly EclairSellerRate SellerRates = new(10_000, 5_000_000, 400, 500, 100, 1_000);

    /// <summary>
    /// The seller's <c>eclair.conf</c> lines after the common ones (<c>eclair.liquidity-ads</c>
    /// of Eclair 0.14.3's <c>reference.conf</c>: <c>funding-rates</c> entries with <c>min-funding-amount-satoshis</c>,
    /// <c>max-funding-amount-satoshis</c>, <c>funding-weight</c>, <c>fee-base-satoshis</c>, <c>fee-basis-points</c> and
    /// <c>channel-creation-fee-satoshis</c>; <c>payment-types</c>; <c>lock-utxos-during-funding</c>).
    /// </summary>
    internal static IReadOnlyList<string> SellerConfigLines(EclairSellerRate rate) =>
    [
        "eclair.liquidity-ads {",
        "  funding-rates = [",
        "    {",
        $"      min-funding-amount-satoshis = {rate.MinFundingSat}",
        $"      max-funding-amount-satoshis = {rate.MaxFundingSat}",
        $"      funding-weight = {rate.FundingWeight}",
        $"      fee-base-satoshis = {rate.FeeBaseSat}",
        $"      fee-basis-points = {rate.FeeBasisPoints}",
        $"      channel-creation-fee-satoshis = {rate.ChannelCreationFeeSat}",
        "    }",
        "  ]",
        "  payment-types = [\"from_channel_balance\"]",
        "  lock-utxos-during-funding = true",
        "}"
    ];

    public async ValueTask DisposeAsync()
    {
        _shared.DisposeAll();
        if (_backend is not null)
            await _backend.DisposeAsync();
    }

    /// <summary>
    /// Writes the last <paramref name="tail"/> lines of Eclair's log to <see cref="Console"/> (the test output); a failed
    /// test also gets a full dump of the namespace under <c>TestResults/cluster/</c>. Does nothing when Eclair never ran.
    /// </summary>
    public Task DumpEclairLogAsync(int tail = 300) => _backend?.DumpEclairLogAsync(tail) ?? Task.CompletedTask;

    /// <summary>Mines <paramref name="blocks"/> blocks to the miner wallet.</summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Waits until Eclair (and <paramref name="extra"/> Eclair clients) and every node in <paramref name="nodes"/> have
    /// processed bitcoind's tip. Eclair has no <c>waitblockheight</c>, so <c>getinfo.blockHeight</c> is polled.
    /// </summary>
    public async Task<uint> WaitAllAtTipAsync(IEnumerable<NLightningTestNode> nodes,
                                              CancellationToken cancellationToken, TimeSpan? timeout = null,
                                              params EclairClient[] extra)
    {
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var tip = (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);
            var eclairHeights = new List<long>();
            var eclairs = extra.Prepend(Eclair).ToList();
            if (_seller is { } seller && !eclairs.Contains(seller.Client))
                eclairs.Add(seller.Client);
            foreach (var eclair in eclairs)
                eclairHeights.Add(await eclair.GetBlockHeightAsync(cancellationToken));
            var ours = nodeList.Select(n => (n.Name,
                                             Height: n.IsRunning ? n.BlockchainMonitor.LastProcessedBlockHeight : 0))
                               .ToList();
            if (eclairHeights.All(h => h == tip) && ours.All(o => o.Height == tip))
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Not at the tip {tip} in time: eclair {string.Join("/", eclairHeights)}, "
                  + string.Join(", ", ours.Select(o => $"{o.Name} {o.Height}")));

            await Task.Delay(200, cancellationToken);
        }
    }

    public async Task<uint> MineAndWaitAsync(int blocks, IEnumerable<NLightningTestNode> nodes,
                                             CancellationToken cancellationToken, params EclairClient[] extra)
    {
        await MineAsync(blocks, cancellationToken);
        return await WaitAllAtTipAsync(nodes, cancellationToken, null, extra);
    }

    /// <summary>
    /// Sends <paramref name="amount"/> from the miner to a new address of <paramref name="eclair"/> (the fixture's
    /// Eclair by default), mines 6 blocks and waits until Eclair's <c>onchainbalance.confirmed</c> grew.
    /// </summary>
    public async Task FundEclairWalletAsync(LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                            CancellationToken cancellationToken, EclairClient? eclair = null)
    {
        eclair ??= Eclair;
        var before = (await eclair.OnchainBalanceAsync(cancellationToken))["confirmed"]!.GetValue<long>();
        var address = await eclair.GetNewAddressAsync(cancellationToken);
        await Bitcoin.Rpc.SendToAddressAsync(NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                                             NBitcoin.Money.Satoshis((long)amount.Satoshi),
                                             cancellationToken: cancellationToken);
        // The fixture's Eclair and a running seller are waited for anyway
        await MineAndWaitAsync(6, nodes, cancellationToken,
                               eclair == Eclair || eclair == _seller?.Client ? [] : [eclair]);
        await Poll.UntilAsync(async () =>
                                  (await eclair.OnchainBalanceAsync(cancellationToken))["confirmed"]!
                                 .GetValue<long>() >= before + (long)amount.Satoshi,
                              TimeSpan.FromSeconds(60), "Eclair sees its deposit confirmed", cancellationToken);
    }

    /// <summary>
    /// Restarts Eclair (its data is kept) at the same address (<see cref="EclairAddress"/>) and waits until it is at
    /// the tip.
    /// </summary>
    public Task RestartEclairAsync(CancellationToken cancellationToken) =>
        Backend.RestartEclairAsync(cancellationToken);

    private ClusterEclairBackend Backend
    {
        get
        {
            SkipIfUnavailable();
            return _backend ?? throw new InvalidOperationException(UnavailableReason ?? ConfigurationError);
        }
    }
}

/// <summary>
/// An Eclair reachable from the tests: its API client, its node id and the <c>pubkey@host:port</c> our nodes dial
/// (its pod IP).
/// </summary>
public sealed record EclairEndpoint(EclairClient Client, string NodeId, string Address);

/// <summary>One <c>eclair.liquidity-ads.funding-rates</c> entry (NL-850).</summary>
public sealed record EclairSellerRate(
    uint MinFundingSat,
    uint MaxFundingSat,
    ushort FundingWeight,
    uint FeeBaseSat,
    ushort FeeBasisPoints,
    uint ChannelCreationFeeSat);