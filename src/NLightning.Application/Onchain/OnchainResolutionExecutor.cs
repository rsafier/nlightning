using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain;

using Accounting;
using Channels.Safety;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fees;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Payments;
using Reorg;

/// <summary>
/// The on-chain resolution loop (BOLT 5 plan §3.2 steps 4-5, O6-T2): for every channel in
/// <see cref="ChannelState.OnchainResolving"/>, under its lock and in one save, the executor marks confirmed spends
/// <see cref="OutputResolutionState.Resolved"/> and 100-deep ones <see cref="OutputResolutionState.Irrevocable"/>,
/// applies the actions of the channel's <see cref="IOutputResolver"/> (by close kind), and persists
/// <see cref="ChannelState.Closed"/> once the funding spend and every output are irrevocably resolved; after the lock it
/// publishes the broadcasts (<see cref="IChainBroadcaster"/>), follows the new watches, raises the switch events and
/// logs the alerts.
/// </summary>
/// <remarks>
/// <para>Resolvers come from the round's scope (<c>IEnumerable&lt;IOutputResolver&gt;</c>, registered by the resolver
/// lanes); a scoped <see cref="IUnitOfWork"/> they take is the round's own, so they read what the round staged. A close
/// kind without a resolver is only marked and aged: its outputs stay pending (logged once), so such a channel never
/// closes, which is what "unresolved" means.</para>
/// <para>Persist before broadcast (D4): a broadcast's row is saved with the decision that made it; the chain monitor
/// sends pending rows again after every block, so a refused or lost publish is retried.</para>
/// <para>A new watch only catches spends in blocks the chain monitor processes after it is tracked, and the monitor
/// goes on processing blocks while a channel's round runs (BOLT 5: "MUST monitor the blockchain for transactions that
/// spend any output that is not irrevocably resolved"). So once a new watch is tracked, the blocks from its parent's
/// confirmation (the commitment's height, the spend's height, our broadcast's confirmation, else the monitor's last
/// processed height read before the round) up to bitcoind's tip are scanned through <see cref="IBitcoinChainService"/>
/// (when registered) for a spend already mined, which is handled like a monitor event and recorded on the watch
/// (<see cref="CatchUpSpendsAsync(ChannelId, IReadOnlyList{WatchedOutpointModel}, uint, CancellationToken)"/>). A scan
/// never starts further back than <see cref="OnchainOptions.CatchUpScanMaxBlocks"/> below the tip (NL-313; a warning
/// says when the bound clips a lower bound, a mainnet fallback to the commitment's height included).</para>
/// <para>After a restart (NL-311) the monitor tracks every saved watch again, but a crash between a save and the
/// tracking (or between the monitor's block save and the executor's handling of the spend) leaves blocks it already
/// processed unscanned for those watches. So after the first round in this process (the time-critical resolution of
/// every channel comes first) the saved watch of every unresolved output (<see cref="OutputResolutionState.Pending"/>,
/// <see cref="OutputResolutionState.Waiting"/>, <see cref="OutputResolutionState.Broadcast"/>) is caught up the same
/// way, from its parent's height (or from the spend recorded on it), in one scan for all channels
/// (<see cref="CatchUpSavedWatchesAsync"/>); a scan that could not reach bitcoind's tip resumes in the next round at
/// the first block it did not scan.</para>
/// <para>Reorgs (BOLT 5 plan §3.8, O6-T3, NL-292): every block round first checks the chain facts the rows rely on. An
/// output recorded <see cref="OutputResolutionState.Resolved"/> whose watched spend the chain monitor rolled back is
/// unresolved again (<see cref="OutputResolutionState.Broadcast"/> when our transaction resolves it, which is made
/// pending again if it was abandoned, else <see cref="OutputResolutionState.Pending"/>), so it is never aged to
/// irrevocable from a disconnected block. When the funding spend's block is no longer in the active chain the channel
/// is paused (no aging, no resolver, no bumping, never Closed) and stays <see cref="ChannelState.OnchainResolving"/>:
/// never back to Open. Its funding outpoint stays watched, so the spend is classified again when it confirms (the same
/// transaction moves the close to its new block; another one replaces the close, the watcher ignores the old rows). Our
/// own commitment is made pending again (rebroadcast every block); after the peer's commitment has been gone for
/// <see cref="OnchainOptions.ReorgGraceBlocks"/> blocks our latest local commitment is broadcast (its stored row made
/// pending again, else signed through <see cref="LocalCommitmentBroadcastBuilder"/>; never after data loss, and the
/// signer refuses a revoked one).</para>
/// </remarks>
public sealed class OnchainResolutionExecutor : IOnchainResolutionExecutor
{
    private readonly IChainBroadcaster _chainBroadcaster;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<OnchainResolutionExecutor> _logger;
    private readonly OnchainOptions _options;
    private readonly IOutpointWatcher _outpointWatcher;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly HashSet<ChannelCloseKind> _missingResolverLogged = [];
    private readonly ConcurrentDictionary<ChannelId, uint> _fundingSpendGoneSince = new();
    private readonly ConcurrentDictionary<ChannelId, byte> _graceBroadcastDone = new();
    private readonly ConcurrentDictionary<ChannelId, byte> _savedWatchesCaughtUp = new();
    private readonly ConcurrentDictionary<ChannelId, uint> _savedWatchesScannedTo = new();
    private readonly SpliceReorgMonitor _spliceReorgMonitor;
    private readonly DiscardedSpliceReservations _discardedSpliceReservations;
    private readonly RecordedFundingSpendReplay _recordedFundingSpendReplay;
    private readonly FundingReconfirmGraceMonitor _fundingReconfirmGrace;
    private readonly ConcurrentDictionary<ChannelId, byte> _noCloseLogged = new();
    private readonly TimeProvider _timeProvider;

    private readonly Lock _gate = new();
    private Task _loop = Task.CompletedTask;
    private uint? _scheduledHeight;
    private bool _looping;

    public OnchainResolutionExecutor(IChainBroadcaster chainBroadcaster, IChannelLockProvider channelLockProvider,
                                     IChannelMemoryRepository channelMemoryRepository,
                                     ILogger<OnchainResolutionExecutor> logger, IOutpointWatcher outpointWatcher,
                                     IServiceScopeFactory serviceScopeFactory,
                                     IOptions<OnchainOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chainBroadcaster = chainBroadcaster;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _outpointWatcher = outpointWatcher;
        _serviceScopeFactory = serviceScopeFactory;
        _options = options?.Value ?? new OnchainOptions();
        _spliceReorgMonitor = new SpliceReorgMonitor(channelMemoryRepository, logger, serviceScopeFactory);
        _discardedSpliceReservations = new DiscardedSpliceReservations(channelLockProvider, channelMemoryRepository,
                                                                       logger, serviceScopeFactory,
                                                                       _options.IrrevocableDepth);
        _recordedFundingSpendReplay =
            new RecordedFundingSpendReplay(channelMemoryRepository, logger, serviceScopeFactory);
        _fundingReconfirmGrace = new FundingReconfirmGraceMonitor(channelMemoryRepository, logger, serviceScopeFactory,
                                                                  _options.FundingReconfirmGraceBlocks);

        // NL-292: a funding transaction confirmed again after a reorg moves its channel's short channel id
        if (outpointWatcher is IBlockchainMonitor blockchainMonitor)
        {
            var fundingReconfirmation = new FundingReconfirmationHandler(channelLockProvider, channelMemoryRepository,
                                                                         logger, serviceScopeFactory);
            blockchainMonitor.OnTransactionConfirmed += (_, args) =>
                _ = fundingReconfirmation.HandleAsync(args.WatchedTransaction);
        }
    }

    /// <inheritdoc />
    public void ScheduleRound(uint height)
    {
        lock (_gate)
        {
            _scheduledHeight = Math.Max(_scheduledHeight ?? 0, height);
            if (_looping)
                return;

            _looping = true;
            _loop = Task.Run(LoopAsync);
        }
    }

    /// <inheritdoc />
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task loop;
            lock (_gate)
            {
                if (!_looping)
                    return;
                loop = _loop;
            }

            await loop;
        }
    }

    /// <inheritdoc />
    public async Task RunRoundAsync(uint height, CancellationToken cancellationToken = default)
    {
        // NL-493: once per process, the funding spends the chain monitor recorded before a crash stopped their handling
        try
        {
            await _recordedFundingSpendReplay.ReplayAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Replaying the recorded funding spends at height {Height} failed", height);
        }

        // NL-492: before any channel may close and leave memory, discarded splices whose conflict is irrevocable give
        // their wallet inputs back
        try
        {
            await _discardedSpliceReservations.CheckAsync(height, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Releasing the wallet inputs of discarded splices at height {Height} failed", height);
        }

        var channels = _channelMemoryRepository.FindChannels(c => c.State == ChannelState.OnchainResolving);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ResolveChannelAsync(channel.ChannelId, height, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "On-chain resolution round of channel {ChannelId} at height {Height} failed",
                                 channel.ChannelId, height);
            }
        }

        // SP2-C-T4: a locked splice that left the active chain is reported (the channel keeps operating)
        try
        {
            await _spliceReorgMonitor.CheckAsync(height, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Checking the locked splices at height {Height} failed", height);
        }

        // NL-329: an Open channel whose funding confirmation a reorg rolled back is failed once the reconfirm grace
        // is over without the funding being seen again
        try
        {
            await _fundingReconfirmGrace.CheckAsync(height, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Checking the funding reconfirm graces at height {Height} failed", height);
        }

        // NL-311: after every channel's time-critical round, one scan for spends of the saved watches of the channels
        // not caught up yet in this process that the chain monitor processed before they were tracked (a crash
        // between a save and the tracking)
        var pending = channels.Select(c => c.ChannelId).Where(id => !_savedWatchesCaughtUp.ContainsKey(id)).ToList();
        if (pending.Count == 0)
            return;

        try
        {
            await CatchUpSavedWatchesCoreAsync(pending, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Catching up the saved resolution watches at height {Height} failed", height);
        }
    }

    /// <summary>The locked-splice reorg check of every block round (SP2-C-T4; tests, diagnostics).</summary>
    internal SpliceReorgMonitor SpliceReorgs => _spliceReorgMonitor;

    /// <summary>The NL-493 startup replay of recorded funding spends (tests, diagnostics).</summary>
    internal RecordedFundingSpendReplay FundingSpendReplay => _recordedFundingSpendReplay;

    /// <summary>The NL-329 funding-reconfirm grace check (tests, diagnostics).</summary>
    internal FundingReconfirmGraceMonitor FundingReconfirms => _fundingReconfirmGrace;

    /// <inheritdoc />
    public Task ResolveChannelAsync(ChannelId channelId, uint height, CancellationToken cancellationToken = default) =>
        RunChannelAsync(channelId, height, null, false, cancellationToken);

    /// <inheritdoc />
    /// <remarks>A spend of a funding output other than the channel's current one (a pending splice's, or one a lock
    /// retired, watched since splicing plan §3.6) reaches this path from the channel manager; it goes to the on-chain
    /// watcher, which classifies it against that funding (SP2-C-T1).</remarks>
    public async Task HandleOutputSpentAsync(OutpointSpentEventArgs args,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.SpentTransactionId is not { } spentTxId || args.SpentOutputIndex is not { } spentIndex)
            return;

        if (await GetFundingSpendWatcherAsync(spentTxId, spentIndex) is { } watcher)
        {
            await watcher.HandleFundingSpentAsync(args, cancellationToken);
            return;
        }

        await RunChannelAsync(args.ChannelId, args.BlockHeight, args, false, cancellationToken);
    }

    /// <summary>The on-chain watcher when the spent outpoint is watched as a funding output, else null.</summary>
    private async Task<IOnchainChannelWatcher?> GetFundingSpendWatcherAsync(TxId txId, uint vout)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(txId, vout);
            return watch?.Purpose == WatchedOutpointPurpose.FundingOutput
                       ? scope.ServiceProvider.GetService<IOnchainChannelWatcher>()
                       : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the watch of {TxId}:{Vout}", Display(txId), vout);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CatchUpSpendsAsync(ChannelId channelId, IReadOnlyList<WatchedOutpointModel> watches,
                                         uint fromHeight, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watches);
        await CatchUpSpendsAsync(channelId, watches.Select(w => (w, fromHeight)).ToList(), cancellationToken);
    }

    /// <inheritdoc />
    public Task CatchUpSavedWatchesAsync(ChannelId channelId, CancellationToken cancellationToken = default) =>
        CatchUpSavedWatchesCoreAsync([channelId], cancellationToken);

    /// <summary>
    /// NL-311: the saved watch of every output of <paramref name="channelIds"/> that is not resolved yet
    /// (<c>Pending</c>, <c>Waiting</c>, <c>Broadcast</c>) is caught up from its parent's height (the commitment's height,
    /// our broadcast's confirmation, else the commitment's height); a watch whose spend the chain monitor recorded while
    /// the row stayed unresolved (a crash after the block's save) from that spend's height. One scan over the blocks
    /// serves every channel. A channel is marked caught up once the scan reached bitcoind's tip; when it stopped
    /// earlier (the chain could not be read), the next attempt resumes at the first block it did not scan.
    /// </summary>
    private async Task CatchUpSavedWatchesCoreAsync(IReadOnlyList<ChannelId> channelIds,
                                                    CancellationToken cancellationToken)
    {
        var watches = new List<(ChannelId ChannelId, WatchedOutpointModel Watch, uint FromHeight)>();
        var scanning = new List<ChannelId>();
        foreach (var channelId in channelIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saved = await GetSavedWatchesAsync(channelId, cancellationToken);
            if (saved is null || saved.Count == 0)
            {
                MarkSavedWatchesCaughtUp(channelId);
                continue;
            }

            // Blocks below the resume height were scanned for this channel's watches by an earlier, interrupted pass
            var resumeAt = _savedWatchesScannedTo.GetValueOrDefault(channelId);
            scanning.Add(channelId);
            watches.AddRange(saved.Select(w => (channelId, w.Watch, Math.Max(w.FromHeight, resumeAt))));
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Checking {Count} saved resolution watches of channel {ChannelId} for spends "
                                     + "mined before they were tracked", saved.Count, channelId);
        }

        if (scanning.Count == 0)
            return;

        var stoppedAt = await ScanForSpendsAsync(watches, cancellationToken);
        foreach (var channelId in scanning)
        {
            if (stoppedAt is { } next)
                _savedWatchesScannedTo.AddOrUpdate(channelId, next, (_, known) => Math.Max(known, next));
            else
                MarkSavedWatchesCaughtUp(channelId);
        }
    }

    private void MarkSavedWatchesCaughtUp(ChannelId channelId)
    {
        _savedWatchesCaughtUp[channelId] = 0;
        _savedWatchesScannedTo.TryRemove(channelId, out _);
    }

    /// <summary>
    /// The saved watch of every unresolved output of the channel with its lower scan bound (NL-311), read under the
    /// channel's lock; null when the channel is not resolving on chain (nothing to catch up).
    /// </summary>
    private async Task<List<(WatchedOutpointModel Watch, uint FromHeight)>?> GetSavedWatchesAsync(
        ChannelId channelId, CancellationToken cancellationToken)
    {
        var watches = new List<(WatchedOutpointModel Watch, uint FromHeight)>();
        using var scope = _serviceScopeFactory.CreateScope();
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State != ChannelState.OnchainResolving)
                return null;

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var close = await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId);
            if (close is null)
                return null;

            var outputs = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channelId);
            foreach (var output in outputs)
            {
                if (output.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting
                                         or OutputResolutionState.Broadcast))
                    continue;

                var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(output.TransactionId,
                                                                                output.OutputIndex);
                if (watch is null)
                    continue;

                var from = watch.SpentAtHeight ?? await CatchUpFromAsync(unitOfWork, close, null, watch, null);
                watches.Add((watch, from));
            }
        }

        return watches;
    }

    /// <summary>
    /// Scans the blocks from each watch's lower bound up to bitcoind's tip for a spend of a tracked watch that the
    /// chain monitor may have processed before the watch was tracked; a spend found is handled as the monitor's event
    /// would be and recorded on the watch in the same save. Harmless when the monitor raises it too (idempotent).
    /// </summary>
    private Task CatchUpSpendsAsync(ChannelId channelId,
                                    IReadOnlyList<(WatchedOutpointModel Watch, uint FromHeight)> watches,
                                    CancellationToken cancellationToken) =>
        ScanForSpendsAsync(watches.Select(w => (channelId, w.Watch, w.FromHeight)).ToList(), cancellationToken);

    /// <summary>
    /// One pass over the blocks from the lowest bound of <paramref name="watches"/> (of any channels) up to bitcoind's
    /// tip; each spend found is handled for its watch's channel. Returns null when the scan reached the tip, else the
    /// first height it did not scan (the chain could not be read).
    /// </summary>
    private async Task<uint?> ScanForSpendsAsync(
        IReadOnlyList<(ChannelId ChannelId, WatchedOutpointModel Watch, uint FromHeight)> watches,
        CancellationToken cancellationToken)
    {
        if (watches.Count == 0)
            return null;

        IBitcoinChainService? chain;
        using (var scope = _serviceScopeFactory.CreateScope())
            chain = scope.ServiceProvider.GetService<IBitcoinChainService>();
        if (chain is null)
            return null;

        var remaining = new Dictionary<OutPoint, (ChannelId ChannelId, uint From)>();
        foreach (var (channelId, watch, from) in watches)
        {
            var outPoint = new OutPoint(new uint256(watch.TransactionId), watch.OutputIndex);
            remaining[outPoint] = remaining.TryGetValue(outPoint, out var known)
                                      ? (known.ChannelId, Math.Min(known.From, from))
                                      : (channelId, from);
        }

        var lowest = remaining.Values.Min(r => r.From);
        uint tip;
        try
        {
            tip = await chain.GetCurrentBlockHeightAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not check resolution watches for spends already mined");
            return lowest;
        }

        // NL-313: a lower bound far back (a mainnet fallback to the commitment's height) must not mean thousands of
        // block fetches: the scan starts at most CatchUpScanMaxBlocks below the tip (0: unbounded), and says so when
        // it clips one. A spend mined deeper than the bound is still found when the watch sees it from a block on.
        var start = lowest;
        if (_options.CatchUpScanMaxBlocks > 0 && tip > lowest
         && tip - lowest > _options.CatchUpScanMaxBlocks)
        {
            start = tip - _options.CatchUpScanMaxBlocks;
            _logger.LogWarning(
                "The catch-up scan of {Count} resolution watches starts at height {Start} instead of {Lowest}: more "
              + "than {Bound} blocks behind the tip {Tip} (Node:Onchain:CatchUpScanMaxBlocks); a spend mined deeper "
              + "than the bound is found only when the chain monitor sees it from a block on",
                remaining.Count, start, lowest, _options.CatchUpScanMaxBlocks, tip);
        }

        for (var height = start; height <= tip && remaining.Count > 0; height++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Block? block;
            try
            {
                block = await chain.GetBlockAsync(height);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Could not read block {Height} to check resolution watches for spends already mined",
                                 height);
                return height;
            }

            if (block is null)
                return height;

            var blockHash = new Hash(block.GetHash().ToBytes());
            for (var index = 0; index < block.Transactions.Count && remaining.Count > 0; index++)
            {
                var transaction = block.Transactions[index];
                foreach (var input in transaction.Inputs)
                {
                    if (!remaining.TryGetValue(input.PrevOut, out var entry) || height < entry.From)
                        continue;

                    remaining.Remove(input.PrevOut);
                    var spendingTxId = new TxId(transaction.GetHash().ToBytes());
                    _logger.LogWarning("Resolution output {Outpoint} of channel {ChannelId} was spent by {TxId} at "
                                     + "height {Height} before its watch was tracked; handling it now",
                                       input.PrevOut, entry.ChannelId, Display(spendingTxId), height);
                    var args = new OutpointSpentEventArgs(entry.ChannelId,
                                                          new SignedTransaction(spendingTxId, transaction.ToBytes()),
                                                          height, (uint)index,
                                                          new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N,
                                                          blockHash);
                    await RunChannelAsync(entry.ChannelId, height, args, true, cancellationToken);
                }
            }
        }

        return null;
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            uint height;
            lock (_gate)
            {
                if (_scheduledHeight is not { } scheduled)
                {
                    _looping = false;
                    return;
                }

                height = scheduled;
                _scheduledHeight = null;
            }

            try
            {
                await RunRoundAsync(height);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "On-chain resolution round at height {Height} failed", height);
            }
        }
    }

    private async Task RunChannelAsync(ChannelId channelId, uint height, OutpointSpentEventArgs? spent,
                                       bool recordWatchSpend, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();

        // Read before any row: a watch on a transaction not confirmed by then can only be spent in a later block
        var lastProcessedBefore = scope.ServiceProvider.GetService<IBlockchainMonitor>()?.LastProcessedBlockHeight;
        Applied applied;
        WatchedOutpointModel? rewatch = null;
        List<(WatchedOutpointModel, uint)> catchUp = [];
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State != ChannelState.OnchainResolving)
                return;

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var close = await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId);
            if (close is null)
            {
                // NL-493: after a close was retired for its discarded splice, the channel waits for our commitment on
                // the splice funding; said once per process, not every block
                if (_noCloseLogged.TryAdd(channelId, 0))
                    _logger.LogWarning("Channel {ChannelId} is resolving on chain without a recorded funding spend "
                                     + "(a close retired for a splice waits for a commitment on the splice funding)",
                                       channelId);
                return;
            }

            _noCloseLogged.TryRemove(channelId, out _);

            var outputs = (await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channelId))
                         .ToDictionary(o => (o.TransactionId, o.OutputIndex));

            // NL-602: the rows as they were, to tell this round's transitions (rows are immutable records)
            var before = new Dictionary<(TxId, uint), OutputResolutionModel>(outputs);
            var actions = new List<OutputResolverAction>();
            var revived = new List<TxId>();
            var resolver = GetResolver(scope, close.Kind);

            if (spent is null)
            {
                // O6-T3: a spend the chain monitor rolled back is no longer a resolution
                await RevertReorgedSpendsAsync(unitOfWork, channelId, outputs, actions, revived);

                if (!await IsFundingSpendOnChainAsync(scope, close))
                {
                    var paused = await HandleFundingSpendGoneAsync(scope, unitOfWork, channel, close, height, actions,
                                                                   revived,
                                                                   AccountingStage(unitOfWork, channel, close, before,
                                                                       outputs, null, height, cancellationToken),
                                                                   cancellationToken);
                    applied = paused.Applied;
                    rewatch = paused.FundingWatch;
                    goto afterLock;
                }

                if (_fundingSpendGoneSince.TryRemove(channelId, out _))
                {
                    _graceBroadcastDone.TryRemove(channelId, out _);
                    _logger.LogWarning("The funding spend {TxId} of channel {ChannelId} is on chain again at height "
                                     + "{Height}; resolving it", Display(close.CommitmentTransactionId), channelId,
                                       close.SpentAtHeight);
                }
            }

            // A confirmed spend of one of the outputs: resolved at that height (whoever spent it)
            if (spent is not null
             && outputs.TryGetValue((spent.SpentTransactionId!.Value, spent.SpentOutputIndex!.Value), out var row))
            {
                var unchanged = row.State is OutputResolutionState.Irrevocable or OutputResolutionState.Ignored
                             || (row.State == OutputResolutionState.Resolved
                              && row.ResolvedHeight == spent.BlockHeight);
                var resolved = unchanged
                                   ? row
                                   : row with
                                   {
                                       State = OutputResolutionState.Resolved,
                                       ResolvedHeight = spent.BlockHeight
                                   };
                if (!unchanged)
                    Upsert(outputs, actions, resolved);

                if (resolver is not null
                 && ChainTxMapper.TryParse(spent.SpendingTransaction.RawTxBytes, out var spendingTx)
                 && spendingTx is not null)
                    Apply(outputs, actions,
                          await resolver.OnOutputSpentAsync(close, resolved, spendingTx, spent.BlockHeight,
                                                            cancellationToken));
            }

            // A spend found by the catch-up scan is recorded on its watch too (what the monitor's block save does)
            var stageMore = new List<Func<Task>>();
            if (recordWatchSpend && spent is not null)
            {
                stageMore.Add(() => unitOfWork.WatchedOutpointDbRepository.MarkSpentAsync(
                                  spent.SpentTransactionId!.Value, spent.SpentOutputIndex!.Value,
                                  spent.SpendingTransaction.TxId, spent.BlockHeight, spent.BlockHash ?? Hash.Empty));
            }

            // O6-T2: a resolution 100 blocks deep is irrevocable
            foreach (var output in outputs.Values.ToList())
            {
                if (output is { State: OutputResolutionState.Resolved, ResolvedHeight: { } resolvedAt }
                 && Depth(height, resolvedAt) >= _options.IrrevocableDepth)
                    Upsert(outputs, actions, output with { State = OutputResolutionState.Irrevocable });
            }

            if (resolver is not null)
                Apply(outputs, actions,
                      await resolver.ResolveAsync(close, outputs.Values.ToList(), height, cancellationToken));

            // O6-T1: our unconfirmed sweeps, claims and penalties are replaced with a higher fee on schedule
            if (spent is null && scope.ServiceProvider.GetService<ISweepScheduler>() is { } sweepScheduler)
            {
                try
                {
                    Apply(outputs, actions,
                          await sweepScheduler.PlanAsync(close, outputs.Values.ToList(), height, unitOfWork,
                                                         cancellationToken));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Fee bumping of channel {ChannelId} at height {Height} failed", channelId,
                                     height);
                }
            }

            // Plan §3.2 step 5: Closed (and the revocation log dropped) once everything is irrevocably resolved
            var closed = Depth(height, close.SpentAtHeight) >= _options.IrrevocableDepth
                      && outputs.Values.All(o => o.State is OutputResolutionState.Irrevocable
                                                           or OutputResolutionState.Ignored);
            if (closed)
            {
                // Closed is staged on a copy read from the database: the shared model changes only after the save,
                // so a failed save leaves the channel resolving and the next round retries the close
                stageMore.Add(async () =>
                {
                    var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId)
                              ?? throw new InvalidOperationException(
                                     $"Channel {channelId} is resolving on chain but is not in the database");
                    if (stored.State < ChannelState.Closed)
                        stored.UpdateState(ChannelState.Closed);
                    await unitOfWork.ChannelDbRepository.UpdateAsync(stored);
                    await unitOfWork.RevokedCommitmentDbRepository.DeleteByChannelIdAsync(channelId);

                    // BOLT 2 interactive-tx (NL-470): the closed channel's negotiations can never finish, and the
                    // table has no FK to Channels, so its rows go in the same save as the Closed state
                    await unitOfWork.InteractiveTxSessionDbRepository.DeleteByChannelIdAsync(channelId);
                });
            }

            // NL-602: the accounting events of this round's resolutions, in its save
            if (AccountingStage(unitOfWork, channel, close, before, outputs, spent, height, cancellationToken) is
                { } accountingStage)
                stageMore.Add(accountingStage);

            applied = await StageAndSaveAsync(unitOfWork, actions,
                                              stageMore.Count == 0
                                                  ? null
                                                  : async () =>
                                                  {
                                                      foreach (var stage in stageMore)
                                                          await stage();
                                                  }, cancellationToken);
            applied = await WithRevivedAsync(unitOfWork, applied, revived);

            // Where a spend of each new watch may already be: from its parent's confirmation
            foreach (var watch in applied.NewWatches)
                catchUp.Add((watch, await CatchUpFromAsync(unitOfWork, close, spent, watch, lastProcessedBefore)));

            if (closed)
            {
                channel.UpdateState(ChannelState.Closed);
                _channelMemoryRepository.TryRemoveChannel(channelId);
                _logger.LogWarning("Channel {ChannelId} is closed: its funding spend and every output are "
                                 + "irrevocably resolved at height {Height}", channelId, height);
            }

        afterLock:;
        }

        if (rewatch is not null)
            _outpointWatcher.TrackWatchedOutpoint(rewatch);

        await AfterSaveAsync(scope, channelId, applied);
        await CatchUpSpendsAsync(channelId, catchUp, cancellationToken);
    }

    /// <summary>
    /// O6-T3: rows <see cref="OutputResolutionState.Resolved"/> by a spend the chain monitor rolled back (their watch is
    /// no longer spent) are unresolved again; a spend confirmed again at another height moves the resolution there.
    /// </summary>
    private async Task RevertReorgedSpendsAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                Dictionary<(TxId, uint), OutputResolutionModel> outputs,
                                                List<OutputResolverAction> actions, List<TxId> revived)
    {
        foreach (var output in outputs.Values.Where(o => o.State == OutputResolutionState.Resolved).ToList())
        {
            var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(output.TransactionId, output.OutputIndex);
            if (watch is null)
                continue;

            if (watch.SpentAtHeight is { } spentAt)
            {
                if (spentAt != output.ResolvedHeight)
                    Upsert(outputs, actions, output with { ResolvedHeight = spentAt });
                continue;
            }

            _logger.LogWarning("The spend of output {Vout} of {TxId} (channel {ChannelId}) resolved at height "
                             + "{Height} was reorged out; it is unresolved again", output.OutputIndex,
                               Display(output.TransactionId), channelId, output.ResolvedHeight);
            Upsert(outputs, actions, output with
            {
                State = output.ResolvingTransactionId is null
                            ? OutputResolutionState.Pending
                            : OutputResolutionState.Broadcast,
                ResolvedHeight = null
            });

            // Our transaction that lost the output to the reorged-out spend may confirm now: send it again (the
            // resolvers never broadcast a row that has a resolving transaction, so it is published after the save)
            if (output.ResolvingTransactionId is { } ours && !revived.Contains(ours))
            {
                revived.Add(ours);
                actions.Add(new StageWriteAction($"revive {Display(ours)}",
                                                 (uow, _) => uow.BroadcastTransactionDbRepository
                                                                .MarkPendingAsync(ours)));
            }
        }
    }

    /// <summary>
    /// True when the block that holds the funding spend is still bitcoind's block at that height (or the close has no
    /// recorded block, or the chain can't be asked).
    /// </summary>
    private async Task<bool> IsFundingSpendOnChainAsync(IServiceScope scope, ChannelCloseModel close)
    {
        if (close.BlockHash.Equals(Hash.Empty)
         || scope.ServiceProvider.GetService<IBitcoinChainService>() is not { } chain)
            return true;

        try
        {
            if (await chain.GetCurrentBlockHeightAsync() < close.SpentAtHeight)
                return false;

            var hash = await chain.GetBlockHashAsync(close.SpentAtHeight);
            return close.BlockHash.Equals(new Hash(hash.ToBytes()));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not check that the funding spend of channel {ChannelId} is still on chain",
                               close.ChannelId);
            return true;
        }
    }

    /// <summary>
    /// The funding spend of <paramref name="close"/> was reorged out (O6-T3, §3.8): the channel stays
    /// <see cref="ChannelState.OnchainResolving"/>, its funding outpoint stays watched, our commitment is pending again,
    /// and after <see cref="OnchainOptions.ReorgGraceBlocks"/> without the peer's commitment our latest local
    /// commitment is broadcast. Stages and saves under the caller's lock.
    /// </summary>
    private async Task<(Applied Applied, WatchedOutpointModel? FundingWatch)> HandleFundingSpendGoneAsync(
        IServiceScope scope, IUnitOfWork unitOfWork, ChannelModel channel, ChannelCloseModel close, uint height,
        List<OutputResolverAction> actions, List<TxId> revived, Func<Task>? stageMore,
        CancellationToken cancellationToken)
    {
        var channelId = channel.ChannelId;
        var first = false;
        var since = _fundingSpendGoneSince.GetOrAdd(channelId, _ =>
        {
            first = true;
            return height;
        });

        WatchedOutpointModel? fundingWatch = null;
        if (first)
        {
            _logger.LogCritical("The funding spend {TxId} ({Kind}) of channel {ChannelId}, recorded at height "
                              + "{Height}, is no longer in the chain (reorg); the channel stays resolving on chain and "
                              + "waits for a funding spend to confirm again", Display(close.CommitmentTransactionId),
                                close.Kind, channelId, close.SpentAtHeight);
            if (channel.FundingOutput is { TransactionId: { } fundingTxId, Index: { } fundingIndex })
                fundingWatch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(fundingTxId, fundingIndex);
        }

        var revive = new List<TxId>();
        if (close.Kind == ChannelCloseKind.LocalCommitment)
        {
            // Our commitment: it is ours to get back into a block
            revive.Add(close.CommitmentTransactionId);
        }
        else if (height >= since + _options.ReorgGraceBlocks && !_graceBroadcastDone.ContainsKey(channelId))
        {
            var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId);
            var ours = broadcasts.Where(b => b is { Purpose: BroadcastPurpose.LocalCommitment, CommitmentNumber: not null })
                                 .OrderByDescending(b => b.CommitmentNumber)
                                 .FirstOrDefault();
            if (ours is not null)
            {
                revive.Add(ours.TransactionId);
                _graceBroadcastDone[channelId] = 0;
            }
            else if (channel.DataLossDetected)
            {
                _logger.LogCritical("The peer's commitment of channel {ChannelId} is gone after a reorg, but we lost "
                                  + "data: our commitment is not broadcast (the peer must close)", channelId);
                _graceBroadcastDone[channelId] = 0;
            }
            else if (scope.ServiceProvider.GetService<LocalCommitmentBroadcastBuilder>() is { } builder)
            {
                try
                {
                    var signed = builder.Build(channel);
                    actions.Add(new BroadcastAction(new BroadcastTransactionModel(
                                                        signed.Transaction, BroadcastPurpose.LocalCommitment,
                                                        channelId, height, commitmentNumber: signed.CommitmentNumber,
                                                        fee: OnchainTransactionFees.ForCommitment(signed.Transaction,
                                                                                                  channel))));
                    _graceBroadcastDone[channelId] = 0;
                    _logger.LogCritical("The peer's commitment {TxId} of channel {ChannelId} has been out of the chain "
                                      + "for {Blocks} blocks; broadcasting our latest commitment {Number} ({OurTxId})",
                                        Display(close.CommitmentTransactionId), channelId, height - since,
                                        signed.CommitmentNumber, Display(signed.Transaction.TxId));
                }
                catch (Exception e) when (e is InvalidOperationException or Domain.Exceptions.SignerException)
                {
                    _logger.LogCritical(e, "Cannot sign our commitment of channel {ChannelId} after the peer's was "
                                         + "reorged out", channelId);
                    _graceBroadcastDone[channelId] = 0;
                }
            }
        }

        foreach (var txId in revive)
            actions.Add(new StageWriteAction($"revive {Display(txId)}",
                                             (uow, _) => uow.BroadcastTransactionDbRepository.MarkPendingAsync(txId)));

        var applied = await StageAndSaveAsync(unitOfWork, actions, stageMore, cancellationToken);
        return (await WithRevivedAsync(unitOfWork, applied, revived.Concat(revive)), fundingWatch);
    }

    /// <summary>
    /// Adds each revived transaction whose row is <see cref="BroadcastState.Pending"/> after the save to the ones
    /// published (<see cref="IChainBroadcaster.PublishAsync"/> then keeps it for rebroadcast after every block).
    /// </summary>
    private static async Task<Applied> WithRevivedAsync(IUnitOfWork unitOfWork, Applied applied,
                                                        IEnumerable<TxId> revived)
    {
        var toPublish = applied.ToPublish.ToList();
        foreach (var txId in revived)
        {
            if (toPublish.Any(t => t.TransactionId == txId))
                continue;
            if (await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId) is
                { State: BroadcastState.Pending } pending)
                toPublish.Add(pending);
        }

        return toPublish.Count == applied.ToPublish.Count ? applied : applied with { ToPublish = toPublish };
    }

    /// <summary>
    /// The lowest height a spend of <paramref name="watch"/>'s outpoint can be at: the height of its parent (the
    /// commitment, the spend being handled, our confirmed broadcast); for a parent not confirmed when the round began,
    /// the monitor's last processed height read then; else the commitment's height.
    /// </summary>
    private static async Task<uint> CatchUpFromAsync(IUnitOfWork unitOfWork, ChannelCloseModel close,
                                                     OutpointSpentEventArgs? spent, WatchedOutpointModel watch,
                                                     uint? lastProcessedBefore)
    {
        if (watch.TransactionId == close.CommitmentTransactionId)
            return close.SpentAtHeight;

        if (spent is not null && spent.SpendingTransaction.TxId == watch.TransactionId)
            return spent.BlockHeight;

        var broadcast = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(watch.TransactionId);
        return broadcast switch
        {
            { ConfirmedHeight: { } confirmedAt } => Math.Max(confirmedAt, close.SpentAtHeight),
            { State: BroadcastState.Pending } when lastProcessedBefore is { } processed =>
                Math.Max(processed, close.SpentAtHeight),
            _ => close.SpentAtHeight
        };
    }

    private IOutputResolver? GetResolver(IServiceScope scope, ChannelCloseKind kind)
    {
        var resolver = scope.ServiceProvider.GetServices<IOutputResolver>().FirstOrDefault(r => r.CanResolve(kind));
        if (resolver is null)
        {
            bool first;
            lock (_missingResolverLogged)
                first = _missingResolverLogged.Add(kind);
            if (first)
                _logger.LogWarning("No output resolver handles {Kind} closes: their outputs are recorded but not "
                                 + "resolved", kind);
        }

        return resolver;
    }

    /// <summary>
    /// The accounting stage of a round (NL-602, <see cref="OnchainAccounting"/>): null when no row made a transition
    /// that is a money fact (resolved by a spend, given up while counted, or unresolved by a reorg).
    /// </summary>
    private Func<Task>? AccountingStage(IUnitOfWork unitOfWork, ChannelModel channel, ChannelCloseModel close,
                                        IReadOnlyDictionary<(TxId, uint), OutputResolutionModel> before,
                                        IReadOnlyDictionary<(TxId, uint), OutputResolutionModel> outputs,
                                        OutpointSpentEventArgs? spent, uint height,
                                        CancellationToken cancellationToken)
    {
        var any = outputs.Any(o => GetTransition(before.GetValueOrDefault(o.Key), o.Value, spent)
                                != AccountingTransition.None);
        return any
                   ? () => StageAccountingAsync(unitOfWork, channel, close, before, outputs, spent, height,
                                                cancellationToken)
                   : null;
    }

    /// <summary>
    /// Stages the round's accounting events in its unit of work: an <see cref="AccountingEventKind.OutputResolved"/>,
    /// <see cref="AccountingEventKind.PenaltyClaimed"/> or <see cref="AccountingEventKind.BreachLoss"/> for the row a
    /// spend resolved (only on its move to <see cref="OutputResolutionState.Resolved"/>, so a replay writes nothing),
    /// a loss for a counted row given up, and a reversal for a resolution a reorg undid. Never throws: a failure is
    /// logged and the round saves without them.
    /// </summary>
    private async Task StageAccountingAsync(IUnitOfWork unitOfWork, ChannelModel channel, ChannelCloseModel close,
                                            IReadOnlyDictionary<(TxId, uint), OutputResolutionModel> before,
                                            IReadOnlyDictionary<(TxId, uint), OutputResolutionModel> outputs,
                                            OutpointSpentEventArgs? spent, uint height,
                                            CancellationToken cancellationToken)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } accounting)
                return;

            var now = _timeProvider.GetUtcNow();
            AccountingEventModel? closeEvent = null;
            var closeEventRead = false;
            foreach (var (key, row) in outputs.OrderBy(o => o.Value.TransactionId.ToString(), StringComparer.Ordinal)
                                              .ThenBy(o => o.Value.OutputIndex))
            {
                var old = before.GetValueOrDefault(key);
                var transition = GetTransition(old, row, spent);
                if (transition == AccountingTransition.None)
                    continue;

                if (transition == AccountingTransition.Unresolved)
                {
                    // O6-T3: the spend that resolved it was reorged out
                    foreach (var baseKey in OnchainAccounting.ResolutionKeys(row.TransactionId, row.OutputIndex))
                    {
                        var original = await OnchainAccounting.FindAsync(accounting, baseKey, old!.ResolvedHeight,
                                                                         cancellationToken);
                        if (original is null)
                            continue;

                        await OnchainAccounting.StageReversalAsync(accounting, original, old.ResolvedHeight ?? height,
                                                                   now, cancellationToken);
                        break;
                    }

                    // NL-608: the loss of a settled forward's incoming HTLC recorded with that spend
                    await StageUpstreamForwardLossReversalAsync(accounting, channel.ChannelId, close, old!, now,
                                                                cancellationToken);
                    continue;
                }

                if (!closeEventRead)
                {
                    closeEvent = await accounting.GetByKeyAsync(
                                     AccountingEventKeys.ChannelForceClosed(channel.ChannelId,
                                                                            close.CommitmentTransactionId),
                                     cancellationToken);
                    closeEventRead = true;
                }

                var counted = closeEvent is not null
                           && (row.TransactionId == close.CommitmentTransactionId
                                   ? OnchainAccounting.CountedVouts(closeEvent).Contains(row.OutputIndex)
                                   : row.Descriptor == OutputDescriptorKind.DelayedToLocal);
                var data = OutputDescriptorData.TryDecode(row);
                var valueMsat = data is null ? 0 : checked((long)data.AmountSat * 1_000);
                var revoked = OnchainAccounting.IsRevoked(row.Descriptor);

                if (transition == AccountingTransition.Ignored)
                {
                    // NL-608: a settled forward's incoming HTLC we gave up is lost
                    await StageUpstreamForwardLossAsync(unitOfWork, accounting, channel, close, row, data, null,
                                                        height, now, cancellationToken);

                    var ignoredKey = AccountingEventKeys.OutputIgnored(row.TransactionId, row.OutputIndex);
                    if (!counted || await accounting.ExistsAsync(ignoredKey, cancellationToken))
                        continue;

                    accounting.Add(OnchainAccounting.Resolution(
                                       channel, close, row, data,
                                       revoked ? AccountingEventKind.BreachLoss : AccountingEventKind.OutputResolved,
                                       ignoredKey,
                                       OnchainAccounting.Lost(valueMsat, true, AccountingDetailKeys.ResolvedByIgnored),
                                       counted, null, height, now));
                    continue;
                }

                // Resolved by the spend this round handles
                if (!ChainTxMapper.TryParse(spent!.SpendingTransaction.RawTxBytes, out var spender)
                 || spender is null)
                    continue;

                var broadcast = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(spender.TxId);
                var ours = row.ResolvingTransactionId == spender.TxId
                        || broadcast is { Purpose: not BroadcastPurpose.PeerCommitment };
                var flows = ours
                                ? OnchainAccounting.Ours(row, valueMsat, counted, spender, outputs,
                                                         broadcast?.Purpose == BroadcastPurpose.HtlcTransaction,
                                                         // NL-611: a stored sweep's other inputs (the peer's anchor);
                                                         // NL-748: our HTLC transaction's wallet fee inputs
                                                         broadcast is
                                                         {
                                                             Purpose: BroadcastPurpose.Sweep
                                                                   or BroadcastPurpose.AnchorSweep
                                                                   or BroadcastPurpose.HtlcTransaction,
                                                             Fee: { } fee
                                                         }
                                                             ? checked((long)fee.MilliSatoshi)
                                                             : null)
                                : OnchainAccounting.Lost(valueMsat, counted, AccountingDetailKeys.ResolvedByPeer);
                if (data is null)
                    flows = flows with { Note = "the output's value is unknown" };

                var kind = revoked
                               ? ours ? AccountingEventKind.PenaltyClaimed : AccountingEventKind.BreachLoss
                               : AccountingEventKind.OutputResolved;
                var baseEventKey = kind switch
                {
                    AccountingEventKind.PenaltyClaimed =>
                        AccountingEventKeys.PenaltyClaimed(row.TransactionId, row.OutputIndex),
                    AccountingEventKind.BreachLoss => AccountingEventKeys.BreachLoss(row.TransactionId,
                                                                                     row.OutputIndex),
                    _ => AccountingEventKeys.OutputResolved(row.TransactionId, row.OutputIndex)
                };
                var eventKey = await OnchainAccounting.NewKeyAsync(accounting, baseEventKey,
                                                                   cancellationToken);
                if (eventKey is null)
                    continue;

                var ownership = await GetHtlcValueOwnerAsync(unitOfWork, channel.ChannelId, data?.Htlc, ours,
                                                             revoked, ClaimPathOf(data?.Htlc, ours, spender, row));
                accounting.Add(OnchainAccounting.Resolution(channel, close, row, data, kind, eventKey, flows, counted,
                                                            spender.TxId, spent.BlockHeight, now,
                                                            broadcast?.ReplacesTransactionId is not null, ownership));

                // NL-608: the peer took a settled forward's incoming HTLC (its timeout): we paid downstream for nothing
                if (!ours && !revoked)
                    await StageUpstreamForwardLossAsync(unitOfWork, accounting, channel, close, row, data,
                                                        spender.TxId, spent.BlockHeight, now, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Channel {ChannelId}: the accounting events of the on-chain round at height {Height} "
                                + "could not be staged", channel.ChannelId, height);
        }
    }

    /// <summary>
    /// The details that tell the books which off-chain event already owns an HTLC output's value (the coordinator rule
    /// in <see cref="OnchainAccounting"/>): an incoming HTLC we claimed (invoice or forward) and an offered HTLC of ours
    /// the peer took (payment or forward, written off the pending bucket). Empty for any other output, or when the
    /// lookup fails (logged). A penalty of a revoked commitment's incoming HTLC (<paramref name="revoked"/>) takes it
    /// through the revocation path, without the preimage: no invoice or forward booked that value, it is a gain
    /// (NL-602 A2).
    /// </summary>
    private async Task<IReadOnlyList<(string Key, string? Value)>> GetHtlcValueOwnerAsync(
        IUnitOfWork unitOfWork, ChannelId channelId, SpecHtlc? htlc, bool ours, bool revoked, string? claimPath)
    {
        if (htlc is not { } spec)
            return [];

        try
        {
            if (spec.Direction == HtlcDirection.Incoming && ours && !revoked)
            {
                var circuit = unitOfWork.ForwardCircuitDbRepository is { } circuits
                                  ? await circuits.GetByIncomingAsync(channelId, spec.Id)
                                  : null;
                return [(OnchainAccounting.ValueBookedByKey, circuit is null ? "invoice" : "forward")];
            }

            if (spec.Direction == HtlcDirection.Outgoing && !ours)
            {
                // NL-612: only a claim with the preimage paid the payment or forward that booked the value; the peer
                // taking it any other way (the revocation path of our own revoked commitment) is a loss of ours
                if (claimPath != AccountingDetailKeys.ClaimPathPreimage)
                    return
                    [
                        (OnchainAccounting.ClaimedByKey, AccountingDetailKeys.ResolvedByPeer),
                        (AccountingDetailKeys.ClaimPath, claimPath),
                        (OnchainAccounting.BucketKey, OnchainAccounting.PendingBucket)
                    ];

                var origin = unitOfWork.ChannelStateDbRepository is { } state
                                 ? await state.GetHtlcOriginAsync(channelId, new HtlcKey(spec.Direction, spec.Id))
                                 : null;
                return
                [
                    (OnchainAccounting.ClaimedByKey, AccountingDetailKeys.ResolvedByPeer),
                    (AccountingDetailKeys.ClaimPath, claimPath),
                    (OnchainAccounting.ValueBookedByKey, origin?.Kind switch
                    {
                        HtlcOriginKind.Local => "payment",
                        HtlcOriginKind.Forwarded => "forward",
                        _ => null
                    }),
                    (OnchainAccounting.BucketKey, OnchainAccounting.PendingBucket)
                ];
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Channel {ChannelId}: the owner of HTLC {HtlcId}'s value could not be read",
                             channelId, spec.Id);
        }

        return [];
    }

    /// <summary>
    /// Stages the <see cref="AccountingEventKind.ForwardLostOnchain"/> of a forward booked as settled (its circuit
    /// <see cref="ForwardCircuitStatus.Fulfilled"/>) whose incoming HTLC output of the close (<paramref name="row"/>) the
    /// peer took or we gave up (NL-608): the incoming amount is lost (the upstream fulfill never got through). Keyed by
    /// the forward (<see cref="AccountingEventKeys.ForwardLostOnchain"/>, by generation after a reorg's reversal).
    /// </summary>
    private static async Task StageUpstreamForwardLossAsync(IUnitOfWork unitOfWork,
                                                            IAccountingEventDbRepository accounting,
                                                            ChannelModel channel, ChannelCloseModel close,
                                                            OutputResolutionModel row, OutputDescriptorData? data,
                                                            TxId? spenderTxId, uint height, DateTimeOffset now,
                                                            CancellationToken cancellationToken)
    {
        if (!IsIncomingHtlcOfTheClose(row, close) || data?.Htlc is not { Direction: HtlcDirection.Incoming } htlc
                                                  || unitOfWork.ForwardCircuitDbRepository is not { } circuits)
            return;

        var circuit = await circuits.GetByIncomingAsync(channel.ChannelId, htlc.Id);
        if (circuit is not { Status: ForwardCircuitStatus.Fulfilled })
            return;

        var key = await OnchainAccounting.NewKeyAsync(
                      accounting, AccountingEventKeys.ForwardLostOnchain(channel.ChannelId, htlc.Id), cancellationToken);
        if (key is null)
            return;

        accounting.Add(PaymentAccountingEvents.ForwardUpstreamLostOnchain(key, circuit, channel,
                                                                          close.CommitmentTransactionId, spenderTxId,
                                                                          now, height));
    }

    /// <summary>
    /// Stages the reversal of the <see cref="StageUpstreamForwardLossAsync"/> event recorded with the resolution of
    /// <paramref name="resolved"/> that a reorg undid (NL-608).
    /// </summary>
    internal static async Task StageUpstreamForwardLossReversalAsync(IAccountingEventDbRepository accounting,
                                                                     ChannelId channelId, ChannelCloseModel close,
                                                                     OutputResolutionModel resolved,
                                                                     DateTimeOffset now,
                                                                     CancellationToken cancellationToken)
    {
        if (!IsIncomingHtlcOfTheClose(resolved, close) || resolved.HtlcId is not { } htlcId)
            return;

        var baseKey = AccountingEventKeys.ForwardLostOnchain(channelId, htlcId);
        var standing = AccountingConfirmations.FindStanding(
            baseKey, await accounting.GetByKeyPrefixAsync(baseKey, cancellationToken));
        if (standing is null
         || !standing.Details.TryGetValue("cause", out var cause) || cause != PaymentAccountingEvents.UpstreamOnchainCause
         || !standing.Details.TryGetValue(AccountingDetailKeys.CloseTxId, out var closeTxId)
         || closeTxId != close.CommitmentTransactionId.ToString()
         || (resolved.ResolvedHeight is { } height && standing.BlockHeight != height))
            return;

        await OnchainAccounting.StageReversalAsync(accounting, standing, standing.BlockHeight ?? 0, now,
                                                   cancellationToken);
    }

    /// <summary>An incoming HTLC output of the close's commitment (ours or the peer's).</summary>
    private static bool IsIncomingHtlcOfTheClose(OutputResolutionModel row, ChannelCloseModel close) =>
        row.TransactionId == close.CommitmentTransactionId
     && row.Descriptor is OutputDescriptorKind.LocalReceivedHtlc or OutputDescriptorKind.RemoteOfferedHtlc;

    /// <summary>
    /// How the peer took our offered HTLC <paramref name="htlc"/> (output <paramref name="row"/>) with
    /// <paramref name="spender"/> (NL-612): the witness of the spending input carries the HTLC's preimage, takes the
    /// revocation path, or neither. Null for any other output or a spend of ours.
    /// </summary>
    private static string? ClaimPathOf(SpecHtlc? htlc, bool ours, ChainTx spender, OutputResolutionModel row)
    {
        if (htlc is not { Direction: HtlcDirection.Outgoing } spec || ours)
            return null;

        var index = spender.IndexOfInputSpending(row.TransactionId, row.OutputIndex);
        if (index < 0)
            return AccountingDetailKeys.ClaimPathUnknown;

        var witness = spender.Inputs[index].Witness;
        if (HtlcWitnessParser.TryExtractPreimage(witness, spec.PaymentHash, out _))
            return AccountingDetailKeys.ClaimPathPreimage;

        return HtlcWitnessParser.Parse(witness).Path == HtlcSpendPath.Revocation
                   ? AccountingDetailKeys.ClaimPathRevocation
                   : AccountingDetailKeys.ClaimPathUnknown;
    }

    /// <summary>What a row's change in a round is, for the accounting feed.</summary>
    private static AccountingTransition GetTransition(OutputResolutionModel? before, OutputResolutionModel after,
                                                      OutpointSpentEventArgs? spent)
    {
        if (before is { State: OutputResolutionState.Resolved }
         && after.State is OutputResolutionState.Pending or OutputResolutionState.Waiting
                                                         or OutputResolutionState.Broadcast)
            return AccountingTransition.Unresolved;

        // Only a row still open before the round: a replayed spend or a resolution moved by a reorg is no new fact
        if (before is not null && before.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting
                                                                                      or OutputResolutionState.Broadcast))
            return AccountingTransition.None;

        if (after.State == OutputResolutionState.Resolved && spent is not null
                                                          && spent.SpentTransactionId == after.TransactionId
                                                          && spent.SpentOutputIndex == after.OutputIndex)
            return AccountingTransition.Resolved;

        return after.State == OutputResolutionState.Ignored
                   ? AccountingTransition.Ignored
                   : AccountingTransition.None;
    }

    /// <summary>Stages every action in the round's unit of work and saves once (nothing is saved when nothing changed).
    /// </summary>
    private static async Task<Applied> StageAndSaveAsync(IUnitOfWork unitOfWork,
                                                        IReadOnlyList<OutputResolverAction> actions,
                                                        Func<Task>? stageMore, CancellationToken cancellationToken)
    {
        var toPublish = new List<BroadcastTransactionModel>();
        var toTrack = new List<WatchedOutpointModel>();
        var staged = false;

        foreach (var upsert in actions.OfType<UpsertOutputAction>())
        {
            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(upsert.Output);
            staged = true;
        }

        foreach (var broadcast in actions.OfType<BroadcastAction>())
        {
            var transaction = broadcast.Transaction;
            var stored = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(
                             transaction.TransactionId);
            if (stored is null)
            {
                unitOfWork.BroadcastTransactionDbRepository.Add(transaction);
                toPublish.Add(transaction);
                staged = true;
            }
            else if (stored.State == BroadcastState.Pending && toPublish.All(t => t.TransactionId != stored.TransactionId))
            {
                toPublish.Add(stored);
            }
            else if (stored.State == BroadcastState.Abandoned
                  && await unitOfWork.BroadcastTransactionDbRepository.MarkPendingAsync(stored.TransactionId))
            {
                // The resolver needs a transaction given up earlier (e.g. a penalty prepared from the mempool and
                // abandoned when its commitment left it, which confirmed later: rebuilt, it has the same txid, since a channel's penalties always pay to
                // the one address RevokedCommitDataSource chose for it)
                stored.MarkPending();
                toPublish.Add(stored);
                staged = true;
            }
        }

        foreach (var watch in actions.OfType<WatchOutpointAction>().Select(a => a.Watch))
        {
            if (toTrack.Any(t => t.TransactionId == watch.TransactionId && t.OutputIndex == watch.OutputIndex)
             || await unitOfWork.WatchedOutpointDbRepository.GetAsync(watch.TransactionId, watch.OutputIndex)
                    is not null)
                continue;

            unitOfWork.WatchedOutpointDbRepository.Add(watch);
            toTrack.Add(watch);
            staged = true;
        }

        foreach (var stage in actions.OfType<StageWriteAction>())
        {
            await stage.Stage(unitOfWork, cancellationToken);
            staged = true;
        }

        if (stageMore is not null)
        {
            await stageMore();
            staged = true;
        }

        if (staged)
            await unitOfWork.SaveChangesAsync();

        return new Applied(staged, toPublish, toTrack,
                           actions.OfType<RaiseChannelEventAction>().Select(a => a.Event).ToList(),
                           actions.OfType<AlertAction>().ToList());
    }

    private async Task AfterSaveAsync(IServiceScope scope, ChannelId channelId, Applied applied)
    {
        foreach (var watch in applied.NewWatches)
            _outpointWatcher.TrackWatchedOutpoint(watch);

        foreach (var transaction in applied.ToPublish)
        {
            try
            {
                if (!await _chainBroadcaster.PublishAsync(transaction))
                    _logger.LogWarning("{Purpose} {TxId} of channel {ChannelId} was refused; it is sent again after "
                                     + "the next block", transaction.Purpose, Display(transaction.TransactionId),
                                       channelId);
                else
                    _logger.LogInformation("Published {Purpose} {TxId} of channel {ChannelId}", transaction.Purpose,
                                           Display(transaction.TransactionId), channelId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Publishing {Purpose} {TxId} of channel {ChannelId} failed; it is sent again "
                                  + "after the next block", transaction.Purpose, Display(transaction.TransactionId),
                                 channelId);
            }
        }

        if (applied.Events.Count > 0
         && scope.ServiceProvider.GetService<Domain.Channels.Interfaces.IHtlcSwitch>() is { } htlcSwitch)
        {
            foreach (var channelEvent in applied.Events)
            {
                try
                {
                    await htlcSwitch.HandleAsync(channelEvent, CancellationToken.None);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "HTLC switch failed on on-chain {Event} for HTLC {HtlcId} of channel "
                                      + "{ChannelId}", channelEvent.GetType().Name, channelEvent.HtlcId,
                                     channelEvent.ChannelId);
                }
            }
        }

        foreach (var alert in applied.Alerts)
        {
            _logger.LogCritical("[{RequirementId}] Channel {ChannelId}: {Alert}", alert.RequirementId, channelId,
                                alert.Message);
            alert.Emitted?.Invoke();
        }
    }

    /// <summary>Applies a resolver's upserts to the working set (so later steps see them) and keeps every action.</summary>
    private static void Apply(Dictionary<(TxId, uint), OutputResolutionModel> outputs,
                              List<OutputResolverAction> actions, IReadOnlyList<OutputResolverAction>? more)
    {
        if (more is null)
            return;

        foreach (var action in more)
        {
            if (action is UpsertOutputAction upsert)
                outputs[(upsert.Output.TransactionId, upsert.Output.OutputIndex)] = upsert.Output;
            actions.Add(action);
        }
    }

    private static void Upsert(Dictionary<(TxId, uint), OutputResolutionModel> outputs,
                               List<OutputResolverAction> actions, OutputResolutionModel output)
    {
        outputs[(output.TransactionId, output.OutputIndex)] = output;
        actions.Add(new UpsertOutputAction(output));
    }

    /// <summary>How many blocks deep a transaction confirmed at <paramref name="height"/> is (1 in the tip block).</summary>
    private static uint Depth(uint tip, uint height) => tip >= height ? tip - height + 1 : 0;

    private static string Display(TxId txId) => new uint256(txId).ToString();

    private enum AccountingTransition
    {
        None,
        Resolved,
        Ignored,
        Unresolved
    }

    private sealed record Applied(
        bool Saved,
        IReadOnlyList<BroadcastTransactionModel> ToPublish,
        IReadOnlyList<WatchedOutpointModel> NewWatches,
        IReadOnlyList<Domain.Channels.Commitments.Events.IChannelDomainEvent> Events,
        IReadOnlyList<AlertAction> Alerts);
}