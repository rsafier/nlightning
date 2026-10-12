using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Services;

using Application.Accounting;
using Application.Accounting.Books;
using Application.Accounting.Financial;
using Application.Accounting.Prices;
using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Fees;
using Application.Channels.RoutingPolicies;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Splicing;
using Application.InteractiveTx;
using Application.Node.Services;
using Application.Onchain.Fees;
using Application.Onchain.Interfaces;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
using Application.Payments.Trampoline;
using Domain.Accounting.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.KeyIndexes;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Interfaces;
using Domain.Gossip.Addresses;
using Domain.Node.Fencing;
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
using Infrastructure.VlsSigning;

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
    private readonly FinancialBooksProjector? _financialBooks;
    private readonly IAccountingBackfill? _accountingBackfill;
    private readonly TrampolineRelayService? _trampolineRelayService;
    private readonly IOnchainResolutionExecutor? _onchainResolutionExecutor;

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
                             PriceValuationService? priceValuation = null,
                             FinancialBooksProjector? financialBooks = null,
                             TrampolineRelayService? trampolineRelayService = null,
                             IOnchainResolutionExecutor? onchainResolutionExecutor = null)
    {
        _onchainResolutionExecutor = onchainResolutionExecutor;
        _trampolineRelayService = trampolineRelayService;
        _priceValuation = priceValuation;
        _financialBooks = financialBooks;
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

            // NL-1392: never hand out a key index of a backed-up channel the database lacks; refuse opens until the
            // restore when asked; say which channels share their keys
            await RaiseKeyIndexesFromBackupFileAsync(stoppingToken);
            await ApplyOpenHoldUntilRestoreAsync();
            await WarnSharedKeyIndexesAsync();

            // Open the accounting feed with the node's balances, once per node (NL-602 A1-T6): before the peers
            // connect and the chain monitor runs, so the database does not change while it is read
            await EnsureAccountingCutoverAsync(stoppingToken);

            // Load the wallet (UTXO set, fee input reservations, channel locks, last processed height) before any peer
            // connects: a splice a peer resumes right after the restart signs reserved wallet inputs (NL-600)
            await _blockchainMonitor.LoadWalletAsync(stoppingToken);

            // NL-1410: finish orphan recovery before peers can reserve inputs for a new negotiation. Otherwise a
            // sweep can see a just-persisted reservation before the contributor records its in-process ownership.
            if (_interactiveTxContributor is not null)
                await _interactiveTxContributor.ReleaseOrphanedReservationsAsync(stoppingToken);

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

            // Resume the unfinished trampoline relays (NL-875 TR3): their mpp_timeout and leg watchdogs, once every
            // channel is loaded and the payments (the relays' outgoing legs) are reconciled
            if (_trampolineRelayService is not null)
                await _trampolineRelayService.StartAsync(stoppingToken);

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

            // Project the operational entries into the financial book (NL-602 A3-T4) after the books; nothing unless
            // Accounting:Profile=Financial
            _financialBooks?.Start();

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

        // A stop can arrive while the node is still starting (an orchestrator or an operator): end the start sequence
        // first, so nothing starts after the services below were stopped, and then stop every service, tolerating the
        // ones the start never reached (several refuse a stop when they never ran, NL-1393)
        await StopQuietlyAsync("the start sequence", () => base.StopAsync(cancellationToken));

        // The safety services, the on-chain resolution rounds and the fee rounds stop before the chain monitor and the
        // peers they use, and before the container is disposed: nothing they started saves or broadcasts after this
        // (NL-1355)
        await Task.WhenAll(StopQuietlyAsync("the HTLC expiry monitor", _htlcExpiryMonitor.StopAsync),
                           StopQuietlyAsync("the fee update scheduler", _feeUpdateScheduler.StopAsync),
                           StopQuietlyAsync("the on-chain resolution executor",
                                            () => _onchainResolutionExecutor?.StopAsync(cancellationToken)
                                               ?? Task.CompletedTask));
        await StopQuietlyAsync("the channel failure service", _channelFailureService.StopAsync);

        // The back-valuation stops before the books (NL-602 A3-T2), the books before the sealer that feeds them (A2)
        if (_priceValuation is not null)
            await StopQuietlyAsync("the price valuation", _priceValuation.StopAsync);
        // The financial projector before the operational books it reads (A3-T4)
        if (_financialBooks is not null)
            await StopQuietlyAsync("the financial books", _financialBooks.StopAsync);
        if (_accountingBooks is not null)
            await StopQuietlyAsync("the accounting books", _accountingBooks.StopAsync);

        // The replay pruner and the mempool reactor stop before the chain monitor that drives them
        await Task.WhenAll(StopQuietlyAsync("the onion replay pruner", _onionReplayBlockPruner.StopAsync),
                           StopQuietlyAsync("the mempool reactor", _mempoolReactor.StopAsync),
                           StopQuietlyAsync("the splice auto-bumper",
                                            () => _spliceAutoBumper?.StopAsync() ?? Task.CompletedTask),
                           StopQuietlyAsync("the accounting sealer",
                                            () => _accountingEventSealer?.StopAsync() ?? Task.CompletedTask),
                           StopQuietlyAsync("the accounting backfill",
                                            () => _accountingBackfill?.StopAsync() ?? Task.CompletedTask));

        // The bootstrap dials through the peer manager, so it stops first (NL-113)
        if (_peerBootstrapService is not null)
            await StopQuietlyAsync("the peer bootstrap", () => _peerBootstrapService.StopAsync(cancellationToken));

        // Closing the control connection takes our onion service down before the listener goes
        if (_torOnionService is not null)
            await StopQuietlyAsync("the Tor onion service", _torOnionService.StopAsync);

        await Task.WhenAll(StopQuietlyAsync("the chain monitor", _blockchainMonitor.StopAsync),
                           StopQuietlyAsync("the fee service", _feeService.StopAsync),
                           StopQuietlyAsync("the peer manager", _peerManager.StopAsync),
                           StopQuietlyAsync("the IPC server", _namedPipeIpcService.StopAsync));

        // Peer storage writes its delayed blobs once the peers stopped, before the container is disposed (NL-010)
        if (_peerStorageService is not null)
            await StopQuietlyAsync("peer storage", _peerStorageService.StopAsync);

        _logger.LogInformation("NLTG daemon service stopped");
    }

    /// <summary>
    /// Stops one service as part of the node's stop (NL-1393): a service the start never reached refuses with an
    /// <see cref="InvalidOperationException"/> ("not running"), which is expected when the stop arrived during the start;
    /// any other failure is logged and the stop goes on with the next service, so one service never keeps the others
    /// running or makes the whole stop fail.
    /// </summary>
    private async Task StopQuietlyAsync(string name, Func<Task> stop)
    {
        try
        {
            await stop();
        }
        catch (InvalidOperationException e)
        {
            _logger.LogDebug(e, "Not stopping {Service}: it was not running", name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stopping {Service} was cancelled", name);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not stop {Service}; stopping the rest of the node", name);
        }
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
    /// NL-1392: raises the channel key index past every channel of <c>channel.backup</c> that the database lacks
    /// (<see cref="BackupFileKeyIndexGuard"/>), before the peers start. Not for a VLS signer, whose keys do not follow
    /// our index.
    /// </summary>
    private async Task RaiseKeyIndexesFromBackupFileAsync(CancellationToken cancellationToken)
    {
        if (_scopeFactory is null || _secureKeyManager is VlsSecureKeyManager)
            return;

        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        if (provider.GetService<IChannelBackupService>() is not { } backupService
         || provider.GetService<IChannelKeyIndexReserver>() is not { } reserver
         || provider.GetService<IOptions<ChannelBackupOptions>>()?.Value is not { } backupOptions)
            return;

        try
        {
            await new BackupFileKeyIndexGuard(backupService, reserver, provider.GetService<IChannelKeyIndexGate>(),
                                              _logger)
                 .RaiseAsync(backupOptions.FilePath, _nodeOptions.BitcoinNetwork.ChainHash,
                             _secureKeyManager.GetNodePubKey(),
                             provider.GetRequiredService<IUnitOfWork>().ChannelDbRepository, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not check the channel backup file's key indexes");
        }
    }

    /// <summary>
    /// <c>Node:Backup:HoldOpensUntilRestore</c> (NL-1392): at a start whose database holds no channel row, every
    /// channel open is refused (<see cref="IChannelKeyIndexGate.HoldUntilRestore"/>) until a <c>restorechanbackup</c>
    /// completes or the operator runs <c>openhold --release</c>, so an open accepted before the restore can't take a
    /// backed-up channel's key index. Runs before the peers start.
    /// </summary>
    private async Task ApplyOpenHoldUntilRestoreAsync()
    {
        if (_scopeFactory is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<IOptions<ChannelBackupOptions>>()?.Value is not
            { HoldOpensUntilRestore: true }
         || scope.ServiceProvider.GetService<IChannelKeyIndexGate>() is not { } gate)
            return;

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var channels = await unitOfWork.ChannelDbRepository.GetLocalKeyIndexesAsync();
        if (channels.Count > 0)
        {
            _logger.LogInformation("Node:Backup:HoldOpensUntilRestore is set but the database already holds {Count} "
                                 + "channel(s): channel opens are not held", channels.Count);
            return;
        }

        gate.HoldUntilRestore();
        _logger.LogWarning("Node:Backup:HoldOpensUntilRestore: the database holds no channel, so every channel open is "
                         + "refused until restorechanbackup completes or 'openhold --release' lifts the hold");
    }

    /// <summary>A critical log per group of live channels that share a key index (NL-1392).</summary>
    private async Task WarnSharedKeyIndexesAsync()
    {
        if (_scopeFactory is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        var channels = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelDbRepository
                                  .GetLocalKeyIndexesAsync();
        foreach (var group in channels.Where(c => c.State is not (Domain.Channels.Enums.ChannelState.Closed
                                                                   or Domain.Channels.Enums.ChannelState.Stale))
                                      .GroupBy(c => c.KeyIndex).Where(g => g.Count() > 1))
            _logger.LogCritical("Channels {Channels} share channel key index {Index}: they are key-compromised; close "
                              + "them cooperatively (closechannel), never force close them",
                                string.Join(", ", group.Select(c => c.ChannelId)), group.Key);
    }

    /// <summary>
    /// Raises the key file's last used channel key index to the highest one stored in the channel tables (every row,
    /// whatever its state) and in the database's high-water mark, so a key-file write lost after its channel row was
    /// committed, or a key file older than the database (a replacement host), cannot make the next open reuse that
    /// index (SECURITY_REVIEW SR-19, NL-1388). Then raises the high-water mark to the key file's index: a node that
    /// upgrades in place seeds it from its key file and channel rows.
    /// </summary>
    private async Task ReconcileChannelKeyIndexAsync()
    {
        if (_scopeFactory is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (_secureKeyManager is VlsSecureKeyManager)
        {
            var mappings = scope.ServiceProvider.GetRequiredService<VlsChannelMappingRegistry>();
            foreach (var channel in await unitOfWork.ChannelDbRepository.GetAllAsync())
                mappings.ValidateChannel(channel);
            return;
        }
        var highest = await unitOfWork.ChannelDbRepository.GetHighestLocalKeyIndexAsync();

        // The high-water mark is the local signer's (NL-1388): a remote signer keeps its own allocation journal
        var keyIndexStore = _secureKeyManager is SecureKeyManager
                                ? scope.ServiceProvider.GetService<IChannelKeyIndexStore>()
                                : null;
        var stored = keyIndexStore?.GetHighWater();
        if (stored > highest)
            highest = stored.Value;

        if (_secureKeyManager.EnsureLastUsedChannelIndexAtLeast(highest))
            _logger.LogWarning("The signer's last used channel key index was below the database's highest ({Index}); "
                             + "raised it", highest);

        // Seed or raise the mark only when it is below the key file (a node that upgrades in place); a fenced process
        // cannot write it, which is only a warning here: its claims refuse every new channel anyway
        if (keyIndexStore is null || _secureKeyManager is not SecureKeyManager localKeys)
            return;
        var target = Math.Max(highest, localKeys.LastUsedChannelKeyIndex);
        if (stored >= target)
            return;
        try
        {
            keyIndexStore.Raise(target);
        }
        catch (NodeFencedException e)
        {
            _logger.LogWarning(e, "Could not raise the database's channel key index high-water mark to {Index}: the "
                                + "node write fence refused it", target);
        }
    }
}