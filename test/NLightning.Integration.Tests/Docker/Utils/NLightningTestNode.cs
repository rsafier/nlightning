using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq.Protected;
using NBitcoin;
using NBitcoin.RPC;
using NLightning.Testing.Lnd;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker.Utils;

using Application.Accounting;
using Application.Accounting.Backfill;
using Application.Accounting.Books;
using Application.Accounting.Financial;
using Application.Accounting.Prices;
using Application.Channels.Fees;
using Application.Channels.RoutingPolicies;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Splicing;
using Application.InteractiveTx;
using Application.Onchain.Fees;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
using Application.Payments.Trampoline;
using Daemon.Extensions;
using Daemon.Interfaces;
using Daemon.Services;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Node.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Fixtures;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Transport.Interfaces;
using Infrastructure.Transport.Services;
using Infrastructure.Transport.Tor;
using Mock;

/// <summary>
/// One NLightning node for the Docker tests, built from the daemon's own composition
/// (<see cref="NodeServiceExtensions.AddNltgNodeServices"/>) so the tests exercise the same service graph as
/// <c>nltg</c>. Several can run in one test process (Bob and Carol of the ABCD tests): each has its own name (the
/// prefix of its log lines), port, key and database.
/// </summary>
/// <remarks>
/// The node keeps its key manager and database across <see cref="StopAsync"/> / <see cref="StartAsync"/> (and
/// <see cref="CrashAsync"/>), so a test can restart it and see its channels again. A node made with
/// <see cref="CreateAsync"/> owns its port and SQLite file and releases them in <see cref="DisposeAsync"/>; otherwise the
/// owner releases the port and calls <see cref="DeleteFiles"/>. Server databases (Postgres, SQL Server) are left in
/// their throwaway container. The fee estimation endpoint is replaced by a fixed answer, so the tests never reach
/// the internet.
/// </remarks>
public sealed class NLightningTestNode : IAsyncDisposable
{
    /// <summary>
    /// What <c>PeerService</c> logs when the first message of a connection is not <c>init</c>. Between two of our
    /// nodes this was the responder losing the initiator's <c>init</c> (NL-239, fixed); tests assert it is never
    /// logged.
    /// </summary>
    public const string InitLostLogFragment = "Failed to receive init message";

    /// <summary>
    /// What a node logs (in the exception text) when writing its own <c>init</c> fails. On a simultaneous connect
    /// between two of our nodes this was a live connection looking closed to the writer, which the other end then
    /// kept (NL-240, fixed); tests assert it is never logged.
    /// </summary>
    public const string InitWriteFailedLogFragment = "Error initializing peer communication";

    /// <summary>
    /// The reconnect backoff <see cref="CreateAsync"/> gives its nodes (the daemon starts at 5 s).
    /// </summary>
    public static readonly TimeSpan FastReconnectInitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a new connection between two NLightning nodes must stay up before
    /// <see cref="ConnectToAsync(NLightningTestNode, CancellationToken)"/> counts it (the connect bugs NL-239/NL-240
    /// used to tear a connection down about 100 ms after both ends listed it).
    /// </summary>
    public static readonly TimeSpan ConnectionStableWindow = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_bothEndsConnectedTimeout = TimeSpan.FromSeconds(10);

    private readonly Lazy<RegtestBitcoinEndpoint> _bitcoinEndpoint;
    private readonly Func<LndNodeConnection, CancellationToken, Task<string>>? _lndPeerEndpoint;
    private readonly Action<NodeOptions>? _configureNodeOptions;
    private readonly bool _ownsResources;

    private readonly ConcurrentQueue<string> _nodeLog = new();

    private CrashableTcpService? _tcpService;
    private IFeeService? _feeService;
    private GossipGraphHostedService? _gossipGraph;
    private ServiceProvider? _serviceProvider;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// The node's name, used as the prefix of its log lines (<c>[bob]</c>).
    /// </summary>
    public string Name { get; }

    public TestNodeDatabase Database { get; }

    /// <summary>
    /// The SQLite file, or <c>null</c> when the node runs on a server database.
    /// </summary>
    public string? DatabaseFilePath => Database.SqliteFilePath;

    public ISecureKeyManager SecureKeyManager { get; }
    public int Port { get; }

    /// <summary>
    /// The first wait of the peer manager's reconnect backoff, or <c>null</c> for the daemon's default. Applied on
    /// every <see cref="StartAsync"/>, so set it before starting.
    /// </summary>
    /// <remarks>Applied as <c>NodeOptions.ReconnectInitialDelay</c>, before the caller's own option changes.</remarks>
    public TimeSpan? ReconnectInitialDelay { get; set; }

    /// <summary>
    /// <c>Bitcoin:WatchMempool</c> (default true, BOLT 5 O8): false leaves the chain monitor without the ZMQ
    /// <c>rawtx</c> loop, so nothing reacts to unconfirmed spends. Applied on every <see cref="StartAsync"/>.
    /// </summary>
    public bool WatchMempool { get; set; } = true;

    /// <summary>
    /// <c>Bitcoin:Notifications</c> (NL-1094): <c>Zmq</c> (default) or <c>Poll</c> (RPC only, the ZMQ settings are not
    /// written; <see cref="WatchMempool"/> then polls <c>gettxspendingprevout</c>). The default comes from the
    /// environment variable <see cref="ChainNotificationsVariable"/>, so a whole suite runs in poll mode
    /// (<c>NLTG_CHAIN_NOTIFICATIONS=Poll scripts/run-cluster.sh ...</c>). Applied on every <see cref="StartAsync"/>.
    /// </summary>
    public string ChainNotifications { get; set; } = DefaultChainNotifications;

    /// <summary>The environment variable that sets <see cref="ChainNotifications"/> for every test node.</summary>
    public const string ChainNotificationsVariable = "NLTG_CHAIN_NOTIFICATIONS";

    /// <summary><c>Poll</c> when <see cref="ChainNotificationsVariable"/> says so (any case), <c>Zmq</c> otherwise.</summary>
    public static string DefaultChainNotifications =>
        string.Equals(Environment.GetEnvironmentVariable(ChainNotificationsVariable), "Poll",
                      StringComparison.OrdinalIgnoreCase)
            ? "Poll"
            : "Zmq";

    /// <summary>True when the node follows the chain by RPC polling only (<see cref="ChainNotifications"/>).</summary>
    public bool IsPollMode => string.Equals(ChainNotifications, "Poll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Last changes to the node's services, applied on every <see cref="StartAsync"/> after the daemon's composition and
    /// the test overrides (e.g. a test-only decorator of the HTLC switch). Set it before starting.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// Runs on every <see cref="StartAsync"/> after the gossip graph is loaded and before <c>PeerManager</c> starts
    /// (and connects to the stored peers), e.g. to read the graph with no connection up (BOLT 7 Proof G2 (b)).
    /// </summary>
    public Func<NLightningTestNode, Task>? BeforePeersStart { get; set; }

    /// <summary>
    /// Extra configuration keys (e.g. <c>Gossip:SyncEnabled</c>, <c>Node:Alias</c>), layered over the test node's own
    /// settings on every <see cref="StartAsync"/>, so a key here overrides a default. Empty by default: the node then
    /// behaves as before. Set them before starting.
    /// </summary>
    public IDictionary<string, string?> ExtraConfiguration { get; } = new Dictionary<string, string?>();

    public bool IsRunning => _started;

    /// <summary>
    /// Every log line of the node's own (<c>NLightning.*</c>) categories since it was created, across restarts, for
    /// tests that assert on what the node logged.
    /// </summary>
    public IReadOnlyCollection<string> NodeLog => _nodeLog;

    /// <summary>
    /// How many <see cref="NodeLog"/> lines contain <paramref name="fragment"/>.
    /// </summary>
    public int CountLogLines(string fragment) =>
        _nodeLog.Count(line => line.Contains(fragment, StringComparison.Ordinal));

    public CompactPubKey NodeId => SecureKeyManager.GetNodePubKey();
    public string NodeIdHex => Convert.ToHexString(NodeId).ToLowerInvariant();

    /// <summary>
    /// The <c>pubkey@127.0.0.1:port</c> other in-process nodes connect to.
    /// </summary>
    public string Address => $"{NodeIdHex}@{IPAddress.Loopback}:{Port}";

    private string FeeCacheFilePath { get; }

    public IServiceProvider Services =>
        _serviceProvider ?? throw new InvalidOperationException($"The node {Name} has not been started");

    public IPeerManager PeerManager => Services.GetRequiredService<IPeerManager>();
    public IBlockchainMonitor BlockchainMonitor => Services.GetRequiredService<IBlockchainMonitor>();
    public IChannelManager ChannelManager => Services.GetRequiredService<IChannelManager>();

    public IChannelMemoryRepository ChannelMemoryRepository =>
        Services.GetRequiredService<IChannelMemoryRepository>();

    public RPCClient Bitcoin => _bitcoinEndpoint.Value.Rpc;

    /// <summary>
    /// A SQLite node (the original constructor).
    /// </summary>
    /// <param name="fixture">The running regtest network.</param>
    /// <param name="databaseFilePath">The SQLite file; reused on restart.</param>
    /// <param name="secureKeyManager">The node key; reused on restart.</param>
    /// <param name="port">The port the node listens on.</param>
    /// <param name="configureNodeOptions">Extra <see cref="NodeOptions"/> changes, applied last.</param>
    /// <param name="name">The log prefix.</param>
    public NLightningTestNode(LightningRegtestNetworkFixture fixture, string databaseFilePath,
                              ISecureKeyManager secureKeyManager, int port,
                              Action<NodeOptions>? configureNodeOptions = null, string name = "nltg")
        : this(fixture, name, TestNodeDatabase.Sqlite(databaseFilePath), secureKeyManager, port, configureNodeOptions)
    {
    }

    /// <param name="fixture">The running regtest network.</param>
    /// <param name="name">The log prefix.</param>
    /// <param name="database">The provider and connection string; reused on restart.</param>
    /// <param name="secureKeyManager">The node key; reused on restart.</param>
    /// <param name="port">The port the node listens on.</param>
    /// <param name="configureNodeOptions">Extra <see cref="NodeOptions"/> changes, applied last.</param>
    public NLightningTestNode(LightningRegtestNetworkFixture fixture, string name, TestNodeDatabase database,
                              ISecureKeyManager secureKeyManager, int port,
                              Action<NodeOptions>? configureNodeOptions = null)
        : this(() => RegtestBitcoinEndpoint.FromFixture(fixture), name, database, secureKeyManager, port,
               configureNodeOptions, ownsResources: false, fixture.GetLndPeerEndpointAsync)
    {
        // A node of the shared network needs it running (the cluster backend, NL-820): skip the test otherwise
        fixture.SkipIfUnavailable();
    }

    private NLightningTestNode(Func<RegtestBitcoinEndpoint> bitcoinEndpoint, string name, TestNodeDatabase database,
                               ISecureKeyManager secureKeyManager, int port,
                               Action<NodeOptions>? configureNodeOptions, bool ownsResources,
                               Func<LndNodeConnection, CancellationToken, Task<string>>? lndPeerEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Resolved once (a failed resolution is not cached, so a node built before its fixture was ready retries)
        _bitcoinEndpoint = new Lazy<RegtestBitcoinEndpoint>(bitcoinEndpoint, LazyThreadSafetyMode.PublicationOnly);
        _configureNodeOptions = configureNodeOptions;
        _ownsResources = ownsResources;
        _lndPeerEndpoint = lndPeerEndpoint;
        Name = name;
        Database = database;
        SecureKeyManager = secureKeyManager;
        Port = port;
        FeeCacheFilePath = database.SqliteFilePath is not null
                               ? $"{database.SqliteFilePath}.fee_cache.bin"
                               : Path.Combine(Path.GetTempPath(), $"nlightning_{name}_{Guid.NewGuid():N}.fee_cache.bin");
    }

    /// <summary>
    /// Makes a node that owns its port (from <see cref="PortPoolUtil"/>), a fresh random key and, unless
    /// <paramref name="database"/> says otherwise, its own SQLite file; <see cref="DisposeAsync"/> releases them. Its
    /// reconnect backoff starts at <see cref="FastReconnectInitialDelay"/>. The node is not started.
    /// </summary>
    /// <remarks>
    /// Skips the test, before taking a port, when the shared network cannot run in this process
    /// (<see cref="LightningRegtestNetworkFixture.UnavailableReason"/>, NL-820).
    /// </remarks>
    public static Task<NLightningTestNode> CreateAsync(LightningRegtestNetworkFixture fixture, string name,
                                                       TestNodeDatabase? database = null,
                                                       Action<NodeOptions>? configureNodeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        fixture.SkipIfUnavailable();
        return CreateAsync(() => RegtestBitcoinEndpoint.FromFixture(fixture), name, database, configureNodeOptions,
                           fixture.GetLndPeerEndpointAsync);
    }

    /// <summary>
    /// As <see cref="CreateAsync(LightningRegtestNetworkFixture, string, TestNodeDatabase?, Action{NodeOptions}?)"/>,
    /// for a node on a bitcoind outside the shared regtest network (e.g. the CLN interop fixture's own).
    /// </summary>
    public static Task<NLightningTestNode> CreateAsync(RegtestBitcoinEndpoint bitcoin, string name,
                                                       TestNodeDatabase? database = null,
                                                       Action<NodeOptions>? configureNodeOptions = null) =>
        CreateAsync(() => bitcoin, name, database, configureNodeOptions);

    private static async Task<NLightningTestNode> CreateAsync(Func<RegtestBitcoinEndpoint> bitcoinEndpoint,
                                                              string name, TestNodeDatabase? database,
                                                              Action<NodeOptions>? configureNodeOptions,
                                                              Func<LndNodeConnection, CancellationToken, Task<string>>?
                                                                  lndPeerEndpoint = null)
    {
        var port = await PortPoolUtil.GetAvailablePortAsync();
        database ??= TestNodeDatabase.Sqlite($"nlightning_{name}_{Guid.NewGuid():N}.db");
        return new NLightningTestNode(bitcoinEndpoint, name, database, new FakeSecureKeyManager(), port,
                                      configureNodeOptions, ownsResources: true, lndPeerEndpoint)
        {
            ReconnectInitialDelay = FastReconnectInitialDelay
        };
    }

    /// <summary>
    /// Builds the service graph, migrates the database and starts the fee service, the peer manager and the
    /// blockchain monitor, in the daemon's order.
    /// </summary>
    /// <remarks>
    /// If a step fails, the services started so far are stopped and the service graph is disposed (releasing the
    /// listener, ZMQ and database connections) before the exception propagates, so the node can be started again.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException($"The node {Name} is already running");

        _serviceProvider = BuildServiceProvider();
        var feeServiceStarted = false;
        var peerManagerStarted = false;
        var safetyStarted = false;
        try
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
                await context.Database.MigrateAsync(cancellationToken);
            }

            // A fresh database starts scanning at the current tip; a restarted node resumes from its stored state
            var currentHeight = (uint)await Bitcoin.GetBlockCountAsync(cancellationToken);

            // IFeeService is one shared singleton (AddFeeServices): the instance we start is the one every consumer reads
            _feeService = Services.GetRequiredService<IFeeService>();
            await _feeService.StartAsync(cancellationToken);
            feeServiceStarted = true;
            // As the daemon does (GossipGraphHostedService runs before NltgDaemonService): load the gossip graph and
            // subscribe its pruner before the peers connect and the chain monitor processes a block (G2-T4/G2-T5)
            _gossipGraph = ActivatorUtilities.CreateInstance<GossipGraphHostedService>(Services);
            await _gossipGraph.StartAsync(cancellationToken);
            if (BeforePeersStart is not null)
                await BeforePeersStart(this);
            // As the daemon does: the accounting cutover before the peers and the chain monitor (NL-602 A1-T6)
            await Services.GetRequiredService<AccountingBackfillService>().EnsureCutoverAsync(cancellationToken);
            // As the daemon does: the wallet (UTXO set, fee input reservations, last processed height) before any peer
            // connects, so a splice resumed right after a restart can sign its reserved wallet inputs (NL-600)
            await BlockchainMonitor.LoadWalletAsync(cancellationToken);
            // As the daemon does: load the per-channel routing policies before any forward or channel_update (SP1-G)
            var channelPolicyStore = Services.GetService<ChannelPolicyStore>();
            if (channelPolicyStore is not null)
                await channelPolicyStore.LoadAsync(cancellationToken);
            // As the daemon does: rebuild the retired short channel ids of spliced channels (wave sp2 SP2-B)
            var retiredScidMap = Services.GetService<IRetiredScidMap>();
            if (retiredScidMap is not null)
                await retiredScidMap.LoadAsync(BlockchainMonitor.LastProcessedBlockHeight, cancellationToken);
            await PeerManager.StartAsync(cancellationToken);
            peerManagerStarted = true;
            // As the daemon does: our Tor onion service, registered in the background once the listener is up (nothing
            // unless Node:Tor turns it on; NL-572). Not tied to the caller's token: StopAsync takes it down
            await Services.GetRequiredService<ITorOnionService>().StartAsync(CancellationToken.None);
            // As the daemon does: settle the payments a crash left without an HTLC id once every channel is loaded
            await Services.GetRequiredService<IPaymentOutcomeHandler>().ReconcileInFlightPaymentsAsync(cancellationToken);
            // As the daemon does: resume the unfinished trampoline relays (NL-875 TR3)
            var trampolineRelays = Services.GetService<TrampolineRelayService>();
            if (trampolineRelays is not null)
                await trampolineRelays.StartAsync(cancellationToken);
            // As the daemon does: the N9 safety services and the update_fee rounds (off unless a test enables them)
            Services.GetRequiredService<IChannelFailureService>().Start();
            Services.GetRequiredService<IHtlcExpiryMonitor>().Start();
            await Services.GetRequiredService<IFeeUpdateScheduler>().StartAsync(cancellationToken);
            safetyStarted = true;
            // As the daemon does: BOLT 5 O8, unconfirmed spends of our outputs (before the monitor's mempool loop)
            Services.GetRequiredService<IMempoolReactor>().Start();
            await BlockchainMonitor.StartAsync(currentHeight, cancellationToken);
            // As the daemon does: drop the retired short channel ids that expired while the node was down (SP2-B)
            retiredScidMap?.PruneExpired(BlockchainMonitor.LastProcessedBlockHeight);
            // As the daemon does: release orphaned withdraw reservations (wave m6 W1)
            var walletSpendService = Services.GetService<IWalletSpendService>();
            if (walletSpendService is not null)
                await walletSpendService.ReleaseOrphanedReservationsAsync(cancellationToken);
            // As the daemon does: release interactive-tx reservations no stored negotiation holds (splicing plan IT2)
            var interactiveTxContributor = Services.GetService<WalletInteractiveTxContributor>();
            if (interactiveTxContributor is not null)
                await interactiveTxContributor.ReleaseOrphanedReservationsAsync(cancellationToken);
            // As the daemon does: catch up on splices that reached their depth while we were down (wave sp1 SP1-D)
            var spliceDepthWatcher = Services.GetService<SpliceDepthWatcher>();
            if (spliceDepthWatcher is not null)
                await spliceDepthWatcher.CatchUpAsync(cancellationToken);
            // As the daemon does: prune the onion replay set on every block (NL-327)
            Services.GetRequiredService<OnionReplayBlockPruner>().Start();
            // As the daemon does: seal the accounting events (NL-602)
            Services.GetService<AccountingEventSealerService>()?.Start();
            // As the daemon does: the books after the sealer (NL-602 A2)
            Services.GetService<AccountingBooksService>()?.Start();
            // As the daemon does: the financial projector after the books (NL-602 A3-T4); off unless Profile=Financial
            Services.GetService<FinancialBooksProjector>()?.Start();
            // As the daemon does: the back-valuation after the books (NL-602 A3-T2)
            Services.GetService<PriceValuationService>()?.Start();
            // As the daemon does: the accounting memo backfill in the background (NL-602 A1-T6)
            Services.GetService<AccountingBackfillService>()?.StartMemoBackfill();
            // As the daemon does: the splice auto-bump (wave SPR); nothing while Splice:AutoBumpAfterBlocks is unset
            Services.GetService<SpliceAutoBumper>()?.Start();
            _started = true;
        }
        catch
        {
            await AbortStartAsync(feeServiceStarted, peerManagerStarted, safetyStarted);
            throw;
        }
    }

    /// <summary>
    /// Stops the node the way the daemon does and disposes its service graph. The database and key stay. The
    /// service graph is disposed, and the node counts as stopped, even when a service fails to stop.
    /// </summary>
    public async Task StopAsync()
    {
        if (_serviceProvider is null)
            return;

        try
        {
            if (_started)
            {
                await StopSafetyServicesAsync();
                // As the daemon does: the back-valuation before the books, the books before the sealer (NL-602)
                await (Services.GetService<PriceValuationService>()?.StopAsync() ?? Task.CompletedTask);
                await (Services.GetService<FinancialBooksProjector>()?.StopAsync() ?? Task.CompletedTask);
                await (Services.GetService<AccountingBooksService>()?.StopAsync() ?? Task.CompletedTask);
                await Task.WhenAll(Services.GetRequiredService<OnionReplayBlockPruner>().StopAsync(),
                                   Services.GetRequiredService<IMempoolReactor>().StopAsync(),
                                   Services.GetService<SpliceAutoBumper>()?.StopAsync() ?? Task.CompletedTask,
                                   Services.GetService<AccountingEventSealerService>()?.StopAsync()
                                ?? Task.CompletedTask,
                                   Services.GetService<AccountingBackfillService>()?.StopAsync()
                                ?? Task.CompletedTask);
                // As the daemon does: closing the control connection takes our onion service down before the listener
                await Services.GetRequiredService<ITorOnionService>().StopAsync();
                await Task.WhenAll(BlockchainMonitor.StopAsync(), _feeService!.StopAsync(), PeerManager.StopAsync());
                // Peer storage writes its delayed blobs once the peers stopped, as the daemon does (NL-010)
                await (Services.GetService<IPeerStorageService>()?.StopAsync() ?? Task.CompletedTask);
                // Last, as the daemon does: the ingress writes the pending graph changes once nothing feeds it
                await StopGossipGraphAsync();
            }
        }
        finally
        {
            _started = false;
            await DisposeServiceProviderAsync();
        }
    }

    /// <summary>
    /// Simulates a crash <b>on the wire</b>: every connection is reset at once (the peers get no final
    /// <c>error</c>/<c>warning</c> and no graceful close) and the node stops listening and connecting, then the
    /// services are torn down. The key and the database stay, so <see cref="StartAsync"/> brings the node back as
    /// after a process restart.
    /// </summary>
    /// <remarks>
    /// This is not a crash for persistence: after the reset the services are stopped with the graceful
    /// <see cref="StopAsync"/>, and <c>PeerManager.StopAsync</c> waits for the inbound message loops and disconnects
    /// every peer, so whatever those loops persist is flushed to the database. It covers what the peers see; a crash
    /// between two persist steps needs a hook in the code under test (e.g. <c>CrashingUnitOfWork</c>).
    /// </remarks>
    public async Task CrashAsync()
    {
        if (!_started || _tcpService is null)
            throw new InvalidOperationException($"The node {Name} is not running");

        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{Name}] crashing");
        await _tcpService.CrashAsync();

        // Nothing can reach a peer any more; stop the loops and release the database and ZMQ sockets
        await StopAsync();
    }

    /// <summary>
    /// Sends <paramref name="amount"/> to a new wallet address, mines 6 blocks and waits until the monitor has seen
    /// the deposit with 6 confirmations.
    /// </summary>
    public async Task FundWalletAsync(LightningMoney amount, AddressType addressType,
                                      CancellationToken cancellationToken, bool isChange = false)
    {
        using var scope = Services.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = await walletService.GetUnusedAddressAsync(addressType, isChange);

        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenInBlock = uint.MaxValue;

        void OnWalletMovementDetected(object? _, WalletMovementEventArgs e)
        {
            if (e.WalletAddress == address.Address && e.Amount == amount)
                seenInBlock = e.BlockHeight;
        }

        void OnNewBlockDetected(object? _, NewBlockEventArgs e)
        {
            if (seenInBlock != uint.MaxValue && e.Height >= seenInBlock + 5)
                confirmed.TrySetResult();
        }

        BlockchainMonitor.OnWalletMovementDetected += OnWalletMovementDetected;
        BlockchainMonitor.OnNewBlockDetected += OnNewBlockDetected;
        try
        {
            await Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Address, NBitcoin.Network.RegTest),
                                             Money.Satoshis((long)amount.Satoshi), cancellationToken);
            await MineBlocksAsync(6, cancellationToken);
            await confirmed.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
        }
        finally
        {
            BlockchainMonitor.OnWalletMovementDetected -= OnWalletMovementDetected;
            BlockchainMonitor.OnNewBlockDetected -= OnNewBlockDetected;
        }
    }

    public async Task MineBlocksAsync(int count, CancellationToken cancellationToken)
    {
        await Bitcoin.GenerateToAddressAsync(count, await Bitcoin.GetNewAddressAsync(cancellationToken),
                                             cancellationToken);
    }

    /// <summary>
    /// Connects to an LND node of the fixture at the address its backend names
    /// (<see cref="LightningRegtestNetworkFixture.GetLndPeerEndpointAsync"/>: the Service name on the cluster, which our
    /// node stores and redials after the pod restarted, NL-780). A node made without the
    /// fixture dials the IP behind the gRPC host.
    /// </summary>
    /// <returns>The <c>pubkey@host:port</c> address used.</returns>
    public async Task<string> ConnectToAsync(LndNodeConnection lndNode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lndNode);
        var host = _lndPeerEndpoint is not null
                       ? await _lndPeerEndpoint(lndNode, cancellationToken)
                       : new IPEndPoint(
                             (await Dns.GetHostAddressesAsync(new Uri(lndNode.Host).Host,
                                                              cancellationToken)).First(), 9735).ToString();
        var address = $"{Convert.ToHexString(lndNode.LocalNodePubKeyBytes)}@{host}";

        await PeerManager.ConnectToPeerAsync(new PeerAddressInfo(address)).WaitAsync(cancellationToken);
        return address;
    }

    /// <summary>
    /// Connects to another in-process node over <c>127.0.0.1</c> and returns once both ends list each other and the
    /// connection stayed up for <see cref="ConnectionStableWindow"/>. There is no retry: since NL-239/NL-240 were
    /// fixed a connect between two NLightning nodes must work the first time.
    /// </summary>
    /// <returns>The <c>pubkey@127.0.0.1:port</c> address used.</returns>
    /// <exception cref="InvalidOperationException">Already connected to <paramref name="other"/>.</exception>
    /// <exception cref="TimeoutException">The connection did not come up on both ends, or dropped.</exception>
    public async Task<string> ConnectToAsync(NLightningTestNode other, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other == this)
            throw new ArgumentException("A node cannot connect to itself", nameof(other));

        if (IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId))
            throw new InvalidOperationException($"{Name} is already connected to {other.Name}");

        try
        {
            await PeerManager.ConnectToPeerAsync(new PeerAddressInfo(other.Address)).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The other node connected to us in the meantime; wait below for both ends to agree
        }

        var failure = await WaitForStableConnectionAsync(other, cancellationToken);
        return failure is null ? other.Address : throw new TimeoutException($"{Name} -> {other.Name}: {failure}");
    }

    /// <summary>
    /// Waits until both ends list each other, then checks the connection stays up for
    /// <see cref="ConnectionStableWindow"/>.
    /// </summary>
    /// <returns><c>null</c> for a stable connection, otherwise what went wrong.</returns>
    public async Task<string?> WaitForStableConnectionAsync(NLightningTestNode other,
                                                            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(other);
        try
        {
            await Poll.UntilAsync(() => IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId),
                                  s_bothEndsConnectedTimeout, $"{Name} and {other.Name} connected",
                                  cancellationToken);
        }
        catch (TimeoutException)
        {
            return $"{Name} and {other.Name} did not both list each other within {s_bothEndsConnectedTimeout}";
        }

        return await Poll.HoldsAsync(() => IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId),
                                     ConnectionStableWindow, cancellationToken)
                   ? null
                   : $"the connection between {Name} and {other.Name} dropped within {ConnectionStableWindow}";
    }

    /// <summary>
    /// Whether the peer manager has a live connection to <paramref name="peerId"/>.
    /// </summary>
    public bool IsConnectedTo(CompactPubKey peerId) => _started && PeerManager.GetPeer(peerId) is not null;

    /// <summary>
    /// The EF Core provider the running node's database context actually uses (e.g.
    /// <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>), read from a fresh <see cref="NLightningDbContext"/>.
    /// </summary>
    public string? GetEfProviderName()
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.ProviderName;
    }

    /// <summary>
    /// Opens a channel through the daemon's client handlers and follows it: mines 6 blocks once funding_signed
    /// arrives and returns when <c>channel_ready</c> was sent or received.
    /// </summary>
    /// <remarks>
    /// The open is v1 unless the request sets <see cref="OpenChannelClientRequest.IsDualFunded"/>: a plain open to a
    /// peer with <c>option_dual_fund</c> (NLightning, CLN) is v2 by default since NL-551, and the suites that open
    /// through this helper were proven on v1 channels, so it sets <see cref="OpenChannelClientRequest.ForceV1"/>.
    /// </remarks>
    public async Task<OpenChannelClientSubscriptionResponse> OpenChannelAsync(OpenChannelClientRequest request,
                                                                              CancellationToken cancellationToken)
    {
        if (!request.IsDualFunded)
            request.ForceV1 = true;

        OpenChannelClientResponse openResponse;
        using (var scope = Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                    OpenChannelClientResponse>>();
            openResponse = await handler.HandleAsync(request, cancellationToken)
                                        .WaitAsync(s_openStepTimeout, cancellationToken);
        }

        while (true)
        {
            OpenChannelClientSubscriptionResponse state;
            using (var scope = Services.CreateScope())
            {
                var handler = scope.ServiceProvider
                                   .GetRequiredService<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                                        OpenChannelClientSubscriptionResponse>>();
                // The subscription misses a peer error sent for the temporary channel id, so never wait forever
                state = await handler.HandleAsync(new OpenChannelClientSubscriptionRequest(openResponse.ChannelId),
                                                  cancellationToken)
                                     .WaitAsync(s_openStepTimeout, cancellationToken);
            }

            if (state.ChannelState == ChannelState.V1FundingSigned)
                await MineBlocksAsync(6, cancellationToken);
            else if (state.ChannelState is ChannelState.ReadyForThem or ChannelState.ReadyForUs
                                        or ChannelState.Open)
                return state;
        }
    }

    /// <summary>
    /// Lists the node's channels through the daemon's <c>ListChannels</c> client handler.
    /// </summary>
    public async Task<ListChannelsClientResponse> ListChannelsAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<ListChannelsClientRequest,
                                ListChannelsClientResponse>>();
        return await handler.HandleAsync(new ListChannelsClientRequest(), cancellationToken);
    }

    /// <summary>
    /// Deletes the node's SQLite file (with its WAL/SHM side files) and fee cache. Call after <see cref="StopAsync"/>.
    /// A server database is left alone: it dies with its container.
    /// </summary>
    public void DeleteFiles()
    {
        List<string> files = [FeeCacheFilePath];
        if (DatabaseFilePath is not null)
            files.AddRange([DatabaseFilePath, $"{DatabaseFilePath}-wal", $"{DatabaseFilePath}-shm"]);

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete {file}: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await StopAsync();
        _disposed = true;

        if (!_ownsResources)
            return;

        DeleteFiles();
        PortPoolUtil.ReleasePort(Port);
    }

    public override string ToString() => $"{Name} ({NodeIdHex[..16]}…, :{Port}, {Database.Provider})";

    /// <summary>
    /// Undoes a failed <see cref="StartAsync"/>: stops what was started (best effort; the start failure is the error
    /// that matters) and disposes the service graph.
    /// </summary>
    private async Task AbortStartAsync(bool feeServiceStarted, bool peerManagerStarted, bool safetyStarted)
    {
        try
        {
            if (safetyStarted)
            {
                await StopSafetyServicesAsync();
                await Services.GetRequiredService<IMempoolReactor>().StopAsync();
            }
            if (peerManagerStarted)
            {
                await Services.GetRequiredService<ITorOnionService>().StopAsync();
                await PeerManager.StopAsync();
                await (Services.GetService<IPeerStorageService>()?.StopAsync() ?? Task.CompletedTask);
            }
            if (feeServiceStarted)
                await _feeService!.StopAsync();
            await StopGossipGraphAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{Name}] failed to stop after a failed start: {e.Message}");
        }
        finally
        {
            await DisposeServiceProviderAsync();
        }
    }

    /// <summary>Stops the gossip graph's pruner and ingress (writes the pending graph) if they were started.</summary>
    private async Task StopGossipGraphAsync()
    {
        var gossipGraph = _gossipGraph;
        _gossipGraph = null;
        if (gossipGraph is not null)
            await gossipGraph.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Stops the HTLC deadline monitor, the fail-the-channel service and the fee rounds, before the chain monitor and
    /// the peers they use (the daemon's order).
    /// </summary>
    private async Task StopSafetyServicesAsync()
    {
        await Task.WhenAll(Services.GetRequiredService<IHtlcExpiryMonitor>().StopAsync(),
                           Services.GetRequiredService<IFeeUpdateScheduler>().StopAsync());
        Services.GetRequiredService<IChannelFailureService>().Stop();
    }

    private async Task DisposeServiceProviderAsync()
    {
        var serviceProvider = _serviceProvider;
        _serviceProvider = null;
        _feeService = null;
        _tcpService = null;
        if (serviceProvider is not null)
            await serviceProvider.DisposeAsync();

        // A stopped node holds no database file open, as a stopped daemon process does: Microsoft.Data.Sqlite keeps
        // closed connections pooled (open) for the next start, so a test that replaces the files of a stopped node saw
        // the old ones on macOS hosts, where File.Copy replaces a file by a new inode (NL-825)
        if (Database.Provider != TestDatabaseProvider.Sqlite)
            return;

        using var connection = new SqliteConnection(Database.ConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    private ServiceProvider BuildServiceProvider()
    {
        var endpoint = _bitcoinEndpoint.Value;
        var bitcoin = endpoint.Rpc;

        List<KeyValuePair<string, string?>> inMemoryConfiguration =
        [
            new("Node:Network", "regtest"),
            new("Node:Daemon", "false"),
            new("Database:Provider", Database.ConfigurationProviderName),
            new("Database:ConnectionString", Database.ConnectionString),
            new("Bitcoin:RpcEndpoint", bitcoin.Address.ToString()),
            new("Bitcoin:RpcUser", bitcoin.CredentialString.UserPassword.UserName),
            new("Bitcoin:RpcPassword", bitcoin.CredentialString.UserPassword.Password),
            // A block ZMQ never announced (mined before the subscription reached bitcoind, which takes longer to a
            // cluster pod than to Docker's 127.0.0.1 port) is caught up within about 2 s instead of 60 s; the monitor
            // logs a warning each time
            new("Bitcoin:TipPollInterval", "00:00:01"),
            new("FeeEstimation:CacheFile", FeeCacheFilePath),
            // Deterministic Docker runs: no periodic update_fee (FeeUpdateFlowTests run rounds by hand; a test can turn
            // it on through configureNodeOptions)
            new("Node:FeeUpdates:Enabled", "false"),
            // Hermetic Docker runs: the financial books never ask mempool.space for prices (NL-641); a test that
            // runs Profile=Financial imports its prices (or sets a source through ExtraConfiguration)
            new("Accounting:Prices:Source", "None"),
            // The gossip memory budget (Gossip:MaxMemoryMb, 1 GiB) reads this process's RSS, which the test process
            // shares among every node of a suite: a long run (the CLN suite on the cluster passed 1.1 GiB) refused new
            // channels from gossip and the gossip proofs waited in vain (NL-865, as NL-466 for the reload tests). A
            // test that proves the budget sets it through ExtraConfiguration
            new("Gossip:MaxMemoryMb", "0"),
            new("Bitcoin:WatchMempool", WatchMempool ? "true" : "false")
        ];
        if (IsPollMode)
        {
            // NL-1094: RPC only, no ZMQ settings at all; polled every second (blocks and, with WatchMempool, the
            // mempool spends of the watched outputs)
            inMemoryConfiguration.Add(new("Bitcoin:Notifications", "Poll"));
            inMemoryConfiguration.Add(new("Bitcoin:PollInterval", "00:00:01"));
        }
        else
        {
            inMemoryConfiguration.Add(new("Bitcoin:ZmqHost", endpoint.ZmqHost));
            inMemoryConfiguration.Add(new("Bitcoin:ZmqBlockPort", endpoint.ZmqBlockPort.ToString()));
            inMemoryConfiguration.Add(new("Bitcoin:ZmqTxPort", endpoint.ZmqTxPort.ToString()));
        }

        // A later source overrides an earlier one, so ExtraConfiguration wins over the defaults above
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfiguration)
                                                      .AddInMemoryCollection(ExtraConfiguration)
                                                      .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new TestConsoleLoggerProvider(Name, _nodeLog))
                                              .SetMinimumLevel(LogLevel.Debug));

        // The daemon's composition
        services.AddNltgNodeServices(configuration, SecureKeyManager);

        // Test-only overrides: a fixed fee estimate, our own port and network, and a TCP service CrashAsync can reset
        services.AddFeeServices(_ => CreateFixedFeeHandler());
        services.AddOptions<NodeOptions>()
                .PostConfigure(options =>
                 {
                     options.Features = new FeatureOptions { ChainHashes = [ChainConstants.Regtest] };
                     options.ListenAddresses = [$"{IPAddress.Loopback}:{Port}"];
                     options.BitcoinNetwork = BitcoinNetwork.Regtest;
                     options.Features.ChainHashes = [options.BitcoinNetwork.ChainHash];
                     if (ReconnectInitialDelay is { } reconnectInitialDelay)
                         options.ReconnectInitialDelay = reconnectInitialDelay;
                     _configureNodeOptions?.Invoke(options);
                 });
        services.AddSingleton<TcpService>();
        services.AddSingleton<ITcpService>(sp =>
        {
            _tcpService = new CrashableTcpService(sp.GetRequiredService<TcpService>());
            return _tcpService;
        });
        ConfigureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static HttpMessageHandler CreateFixedFeeHandler()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
               .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                                                 ItExpr.IsAny<CancellationToken>())
               .ReturnsAsync(() => new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent("{\"fastestFee\": 10}")
               });
        return handler.Object;
    }

    /// <summary>
    /// Writes log lines, prefixed with the node's name, to <see cref="Console"/>, which the Docker tests redirect to
    /// the xUnit output.
    /// </summary>
    private sealed class TestConsoleLoggerProvider(string nodeName, ConcurrentQueue<string> nodeLog) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            new TestConsoleLogger(nodeName, categoryName,
                                  categoryName.StartsWith("NLightning.", StringComparison.Ordinal) ? nodeLog : null);

        public void Dispose()
        {
        }

        private sealed class TestConsoleLogger(string nodeName, string categoryName, ConcurrentQueue<string>? nodeLog)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{nodeName}] [{logLevel}] {categoryName}: "
                         + formatter(state, exception)
                         + (exception is null ? string.Empty : $" {exception}");
                nodeLog?.Enqueue(line);
                Console.WriteLine(line);
            }
        }
    }
}