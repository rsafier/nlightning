using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Services;

using Application.Accounting;
using Application.Accounting.Books;
using Application.Accounting.Prices;
using Application.Channels.Fees;
using Application.Channels.RoutingPolicies;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Splicing;
using Application.InteractiveTx;
using Application.Node.Services;
using Application.Onchain.Fees;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
using Domain.Accounting.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Interfaces;
using Domain.Gossip.Addresses;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Transport.Tor;

public class NltgDaemonService : BackgroundService
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelFailureService _channelFailureService;
    private readonly IConfiguration _configuration;
    private readonly IFeeService _feeService;
    private readonly IFeeUpdateScheduler _feeUpdateScheduler;
    private readonly IHtlcExpiryMonitor _htlcExpiryMonitor;
    private readonly ILogger<NltgDaemonService> _logger;
    private readonly IMempoolReactor _mempoolReactor;
    private readonly INamedPipeIpcService _namedPipeIpcService;
    private readonly OnionReplayBlockPruner _onionReplayBlockPruner;
    private readonly IPeerManager _peerManager;
    private readonly NodeOptions _nodeOptions;
    private readonly IPaymentOutcomeHandler _paymentOutcomeHandler;
    private readonly IPeerStorageService? _peerStorageService;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IWalletSpendService? _walletSpendService;
    private readonly WalletInteractiveTxContributor? _interactiveTxContributor;
    private readonly ChannelPolicyStore? _channelPolicyStore;
    private readonly SpliceDepthWatcher? _spliceDepthWatcher;
    private readonly IRetiredScidMap? _retiredScidMap;
    private readonly SpliceAutoBumper? _spliceAutoBumper;
    private readonly IPeerBootstrapService? _peerBootstrapService;
    private readonly ITorOnionService? _torOnionService;
    private readonly GossipOptions? _gossipOptions;
    private readonly FeeEstimationOptions? _feeEstimationOptions;
    private readonly INodeBusyStateMonitor? _busyStateMonitor;
    private readonly INodeDrainState? _nodeDrainState;
    private readonly ShutdownDrainWaiter? _drainWaiter;
    private readonly AccountingEventSealerService? _accountingEventSealer;
    private readonly AccountingBooksService? _accountingBooks;
    private readonly PriceValuationService? _priceValuation;
    private readonly IAccountingBackfill? _accountingBackfill;

    public NltgDaemonService(IBlockchainMonitor blockchainMonitor, IChannelFailureService channelFailureService,
                             IConfiguration configuration, IFeeService feeService,
                             IFeeUpdateScheduler feeUpdateScheduler, IHtlcExpiryMonitor htlcExpiryMonitor,
                             ILogger<NltgDaemonService> logger, INamedPipeIpcService namedPipeIpcService,
                             OnionReplayBlockPruner onionReplayBlockPruner, IOptions<NodeOptions> nodeOptions, IPaymentOutcomeHandler paymentOutcomeHandler,
                             IPeerManager peerManager, ISecureKeyManager secureKeyManager,
                             IMempoolReactor mempoolReactor, IServiceScopeFactory? scopeFactory = null,
                             IPeerStorageService? peerStorageService = null,
                             IWalletSpendService? walletSpendService = null,
                             WalletInteractiveTxContributor? interactiveTxContributor = null,
                             ChannelPolicyStore? channelPolicyStore = null,
                             SpliceDepthWatcher? spliceDepthWatcher = null,
                             IRetiredScidMap? retiredScidMap = null,
                             SpliceAutoBumper? spliceAutoBumper = null,
                             IPeerBootstrapService? peerBootstrapService = null,
                             ITorOnionService? torOnionService = null,
                             IOptions<GossipOptions>? gossipOptions = null,
                             IOptions<FeeEstimationOptions>? feeEstimationOptions = null,
                             INodeBusyStateMonitor? busyStateMonitor = null,
                             INodeDrainState? nodeDrainState = null,
                             ShutdownDrainWaiter? drainWaiter = null,
                             AccountingEventSealerService? accountingEventSealer = null,
                             AccountingBooksService? accountingBooks = null,
                             IAccountingBackfill? accountingBackfill = null,
                             PriceValuationService? priceValuation = null)
    {
        _priceValuation = priceValuation;
        _accountingEventSealer = accountingEventSealer;
        _accountingBooks = accountingBooks;
        _accountingBackfill = accountingBackfill;
        _feeEstimationOptions = feeEstimationOptions?.Value;
        _busyStateMonitor = busyStateMonitor;
        _nodeDrainState = nodeDrainState;
        _drainWaiter = drainWaiter;
        _torOnionService = torOnionService;
        _gossipOptions = gossipOptions?.Value;
        _peerBootstrapService = peerBootstrapService;
        _spliceAutoBumper = spliceAutoBumper;
        _retiredScidMap = retiredScidMap;
        _channelPolicyStore = channelPolicyStore;
        _spliceDepthWatcher = spliceDepthWatcher;
        _interactiveTxContributor = interactiveTxContributor;
        _walletSpendService = walletSpendService;
        _scopeFactory = scopeFactory;
        _peerStorageService = peerStorageService;
        _mempoolReactor = mempoolReactor;
        _blockchainMonitor = blockchainMonitor;
        _channelFailureService = channelFailureService;
        _configuration = configuration;
        _feeService = feeService;
        _feeUpdateScheduler = feeUpdateScheduler;
        _htlcExpiryMonitor = htlcExpiryMonitor;
        _logger = logger;
        _namedPipeIpcService = namedPipeIpcService;
        _onionReplayBlockPruner = onionReplayBlockPruner;
        _peerManager = peerManager;
        _nodeOptions = nodeOptions.Value;
        _paymentOutcomeHandler = paymentOutcomeHandler;
        _secureKeyManager = secureKeyManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var network = _configuration["network"] ?? _configuration["n"] ?? _nodeOptions.BitcoinNetwork;
        var isDaemon = _configuration.GetValue<bool?>("daemon")
                    ?? _configuration.GetValue<bool?>("daemon-child")
                    ?? _nodeOptions.Daemon;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("NLTG Daemon started on {Network} network", network);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Running in daemon mode: {IsDaemon}", isDaemon);

            var pubKey = _secureKeyManager.GetNodePubKey();
            _logger.LogDebug("Our PubKey is {pubKey}", pubKey.ToString());
        }

        try
        {
            // Start the fee service
            await _feeService.StartAsync(stoppingToken);

            // Never hand out a channel key index a stored channel already uses (SECURITY_REVIEW SR-19)
            await ReconcileChannelKeyIndexAsync();

            // Open the accounting feed with the node's balances, once per node (NL-602 A1-T6): before the peers
            // connect and the chain monitor runs, so the database does not change while it is read
            await EnsureAccountingCutoverAsync(stoppingToken);

            // Load the per-channel routing policies before any forward or channel_update (wave sp1 SP1-G); a failure
            // fails the start
            if (_channelPolicyStore is not null)
                await _channelPolicyStore.LoadAsync(stoppingToken);

            // Rebuild the retired short channel ids of spliced channels before any forward can name one (wave sp2
            // SP2-B, D12); entries already expired are refused at the monitor's tip and pruned on every block
            if (_retiredScidMap is not null)
                await _retiredScidMap.LoadAsync(_blockchainMonitor.LastProcessedBlockHeight, stoppingToken);

            // Start the peer manager service
            await _peerManager.StartAsync(stoppingToken);

            // Tor (Node:Tor): our onion service is registered in the background once the listener is up; a Tor that
            // is not running yet only delays it
            LogTorSettings();
            if (_torOnionService is not null)
                await _torOnionService.StartAsync(stoppingToken);

            // BOLT 10 DNS seed bootstrap (NL-113): runs in the background (off unless Node:Bootstrap:Enabled)
            if (_peerBootstrapService is not null)
                await _peerBootstrapService.StartAsync(stoppingToken);

            // Every stored channel is in memory now: settle the payments a crash left without an HTLC id (W2-C)
            await _paymentOutcomeHandler.ReconcileInFlightPaymentsAsync(stoppingToken);

            // Channel safety (N9): fail-the-channel broadcasts (and their resumption), the HTLC deadline monitor and
            // the update_fee rounds of the channels we fund
            _channelFailureService.Start();
            _htlcExpiryMonitor.Start();
            await _feeUpdateScheduler.StartAsync(stoppingToken);

            // BOLT 5 O8: react to unconfirmed spends of our outputs (subscribed before the monitor's mempool loop runs)
            _mempoolReactor.Start();

            // Start the blockchain monitor service
            await _blockchainMonitor.StartAsync(_secureKeyManager.HeightOfBirth, stoppingToken);

            // Drop the retired short channel ids that expired while we were down, now that the tip is known (SP2-B)
            _retiredScidMap?.PruneExpired(_blockchainMonitor.LastProcessedBlockHeight);

            // Release the withdraw reservations a crash left without their broadcast row (wave m6 W1)
            if (_walletSpendService is not null)
                await _walletSpendService.ReleaseOrphanedReservationsAsync(stoppingToken);

            // Release the interactive-tx input reservations no stored negotiation holds any more (splicing plan IT2)
            if (_interactiveTxContributor is not null)
                await _interactiveTxContributor.ReleaseOrphanedReservationsAsync(stoppingToken);

            // Catch up on splices that reached their depth while we were down (wave sp1 SP1-D); resolving the watcher
            // also subscribes it to the chain monitor's confirmations
            if (_spliceDepthWatcher is not null)
                await _spliceDepthWatcher.CatchUpAsync(stoppingToken);

            // Prune the onion replay set on every block (NL-327)
            _onionReplayBlockPruner.Start();

            // Seal the accounting events committed so far and every SealInterval from now on (NL-602)
            _accountingEventSealer?.Start();

            // Post the sealed events to the books and reconcile them every SnapshotInterval (NL-602 A2); nothing while
            // Accounting:Enabled=false
            _accountingBooks?.Start();

            // Give the financial postings their fiat value every Accounting:Prices:FetchInterval (NL-602 A3-T2), after
            // the books; nothing while the books are off
            _priceValuation?.Start();

            // Write the history before the cutover as memo events, in the background (resumes at the next start)
            _accountingBackfill?.StartMemoBackfill();

            // Bump stale splices of ours (wave SPR, SPR-T3); does nothing while Splice:AutoBumpAfterBlocks is unset
            _spliceAutoBumper?.Start();

            // Start the IPC server
            await _namedPipeIpcService.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
                await Task.Delay(1000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stopping NLTG daemon service");
        }
    }

    /// <summary>
    /// The accounting cutover (NL-602 A1-T6). A failure is logged and the node starts anyway: the node must run to
    /// protect its channels. The feed then records nothing until a later start's cutover succeeds (NL-619: the backfill
    /// holds the feed's gate), so that start opens the feed with the node's balances again.
    /// </summary>
    private async Task EnsureAccountingCutoverAsync(CancellationToken cancellationToken)
    {
        if (_accountingBackfill is null)
            return;

        try
        {
            await _accountingBackfill.EnsureCutoverAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "The accounting cutover failed; the node starts without it, the accounting feed "
                              + "records nothing until the next start's cutover succeeds");
        }
    }

    private void LogTorSettings()
    {
        var tor = _nodeOptions.Tor;
        if (!tor.IsEnabled)
            return;

        _logger.LogInformation("Tor {Mode}: peers dialed through {SocksProxy}{Isolation}; onion service {OnionService}",
                               tor.Mode, tor.SocksProxy, tor.StreamIsolation ? " (stream isolation)" : string.Empty,
                               tor.IsOnionServiceEnabled ? $"through {tor.Control}" : "off");

        IReadOnlyList<AddressDescriptor> announced;
        try
        {
            announced = _gossipOptions?.GetAnnounceAddressDescriptors() ?? [];
        }
        catch (ArgumentException)
        {
            announced = [];
        }

        var feeOverHttp = _feeEstimationOptions?.IsSource(FeeEstimationOptions.SourceHttp) ?? false;
        foreach (var warning in TorStartupChecks.GetWarnings(_nodeOptions, announced, feeOverHttp))
            _logger.LogWarning("{Warning}", warning);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("NLTG shutdown requested");

        // The bounded signal drain (NL-592): refuse new activity and give the HTLCs and negotiations in flight a
        // short, bounded chance to resolve before the services stop. An IPC `shutdown --wait` holds the gate
        // already; then its wait ran (or still runs) and this does nothing.
        await DrainOnSignalAsync();

        // The safety services and the fee rounds stop before the chain monitor and the peers they use
        await Task.WhenAll(_htlcExpiryMonitor.StopAsync(), _feeUpdateScheduler.StopAsync());
        _channelFailureService.Stop();

        // The back-valuation stops before the books (NL-602 A3-T2), the books before the sealer that feeds them (A2)
        if (_priceValuation is not null)
            await _priceValuation.StopAsync();
        if (_accountingBooks is not null)
            await _accountingBooks.StopAsync();

        // The replay pruner and the mempool reactor stop before the chain monitor that drives them
        await Task.WhenAll(_onionReplayBlockPruner.StopAsync(), _mempoolReactor.StopAsync(),
                           _spliceAutoBumper?.StopAsync() ?? Task.CompletedTask,
                           _accountingEventSealer?.StopAsync() ?? Task.CompletedTask,
                           _accountingBackfill?.StopAsync() ?? Task.CompletedTask);

        // The bootstrap dials through the peer manager, so it stops first (NL-113)
        if (_peerBootstrapService is not null)
            await _peerBootstrapService.StopAsync(cancellationToken);

        // Closing the control connection takes our onion service down before the listener goes
        if (_torOnionService is not null)
            await _torOnionService.StopAsync();

        await Task.WhenAll(_blockchainMonitor.StopAsync(), _feeService.StopAsync(), _peerManager.StopAsync(),
                           _namedPipeIpcService.StopAsync(), base.StopAsync(cancellationToken));

        // Peer storage writes its delayed blobs once the peers stopped, before the container is disposed (NL-010)
        if (_peerStorageService is not null)
            await _peerStorageService.StopAsync();

        _logger.LogInformation("NLTG daemon service stopped");
    }

    /// <summary>
    /// The signal drain (<c>Node:Shutdown:DrainOnSignalSeconds</c>, default 0 = off, NL-592): the drain gate closes
    /// and the node waits at most that long to go idle; whatever the outcome, the stop then proceeds as before.
    /// Nothing is forced, closed or broadcast here, and an <c>shutdown --wait</c> already holding the gate makes
    /// this a no-op.
    /// </summary>
    private async Task DrainOnSignalAsync()
    {
        var seconds = _nodeOptions.Shutdown.DrainOnSignalSeconds;
        if (seconds <= 0 || _busyStateMonitor is null || _nodeDrainState is null || _drainWaiter is null)
            return;

        if (!_nodeDrainState.TryBeginDrain())
            return;

        _logger.LogInformation("Draining on signal for up to {Seconds}s before the node stops", seconds);
        var result = await _drainWaiter.WaitUntilIdleAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
        if (result.Drained)
        {
            _logger.LogInformation("The signal drain reached an idle node");
            return;
        }

        _nodeDrainState.EndDrain();
        _logger.LogWarning("The signal drain ran out of time with {Htlcs} HTLC(s) in flight and {Negotiations} "
                         + "negotiation(s) mid-flight; stopping anyway (nothing is broadcast; the HTLC expiry "
                         + "monitor and the resolvers run at the next start)", result.Snapshot.HtlcsInFlight,
                           result.Snapshot.NegotiationCount);
    }

    /// <summary>
    /// Raises the key file's last used channel key index to the highest one stored in the channel tables (every row,
    /// whatever its state), so a key-file write lost after its channel row was committed cannot make the next open
    /// reuse that index (SECURITY_REVIEW SR-19).
    /// </summary>
    private async Task ReconcileChannelKeyIndexAsync()
    {
        if (_scopeFactory is null || _secureKeyManager is not SecureKeyManager fileBacked)
            return;

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var highest = await unitOfWork.ChannelDbRepository.GetHighestLocalKeyIndexAsync();
        if (fileBacked.EnsureLastUsedChannelIndexAtLeast(highest))
            _logger.LogWarning("The key file's last used channel key index was below the database's highest ({Index}); "
                             + "raised it", highest);
    }
}