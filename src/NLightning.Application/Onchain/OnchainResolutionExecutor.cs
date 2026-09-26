using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain;

using Channels.Safety;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Fees;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
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
/// (<see cref="CatchUpSpendsAsync(ChannelId, IReadOnlyList{WatchedOutpointModel}, uint, CancellationToken)"/>).</para>
/// <para>After a restart (NL-311) the monitor tracks every saved watch again, but a crash between a save and the
/// tracking (or between the monitor's block save and the executor's handling of the spend) leaves blocks it already
/// processed unscanned for those watches. So before a channel's first round in this process the saved watch of every
/// unresolved output (<see cref="OutputResolutionState.Pending"/>, <see cref="OutputResolutionState.Waiting"/>,
/// <see cref="OutputResolutionState.Broadcast"/>) is caught up the same way, from its parent's height (or from the
/// spend recorded on it) (<see cref="CatchUpSavedWatchesAsync"/>); a scan that could not reach bitcoind's tip is tried
/// again in the next round.</para>
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

    private readonly Lock _gate = new();
    private Task _loop = Task.CompletedTask;
    private uint? _scheduledHeight;
    private bool _looping;

    public OnchainResolutionExecutor(IChainBroadcaster chainBroadcaster, IChannelLockProvider channelLockProvider,
                                     IChannelMemoryRepository channelMemoryRepository,
                                     ILogger<OnchainResolutionExecutor> logger, IOutpointWatcher outpointWatcher,
                                     IServiceScopeFactory serviceScopeFactory,
                                     IOptions<OnchainOptions>? options = null)
    {
        _chainBroadcaster = chainBroadcaster;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _outpointWatcher = outpointWatcher;
        _serviceScopeFactory = serviceScopeFactory;
        _options = options?.Value ?? new OnchainOptions();

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
        var channels = _channelMemoryRepository.FindChannels(c => c.State == ChannelState.OnchainResolving);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // NL-311: the first round of this process scans for spends of the channel's saved watches that the
                // chain monitor processed before they were tracked (a crash between a save and the tracking)
                if (!_savedWatchesCaughtUp.ContainsKey(channel.ChannelId)
                 && await CatchUpSavedWatchesCoreAsync(channel.ChannelId, cancellationToken))
                    _savedWatchesCaughtUp[channel.ChannelId] = 0;

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
    }

    /// <inheritdoc />
    public Task ResolveChannelAsync(ChannelId channelId, uint height, CancellationToken cancellationToken = default) =>
        RunChannelAsync(channelId, height, null, false, cancellationToken);

    /// <inheritdoc />
    public Task HandleOutputSpentAsync(OutpointSpentEventArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.SpentTransactionId is null || args.SpentOutputIndex is null)
            return Task.CompletedTask;

        return RunChannelAsync(args.ChannelId, args.BlockHeight, args, false, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CatchUpSpendsAsync(ChannelId channelId, IReadOnlyList<WatchedOutpointModel> watches,
                                         uint fromHeight, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watches);
        await CatchUpSpendsAsync(channelId, watches.Select(w => (w, fromHeight)).ToList(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task CatchUpSavedWatchesAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        if (await CatchUpSavedWatchesCoreAsync(channelId, cancellationToken))
            _savedWatchesCaughtUp[channelId] = 0;
    }

    /// <summary>
    /// NL-311: the saved watch of every output of the channel that is not resolved yet (<c>Pending</c>,
    /// <c>Waiting</c>, <c>Broadcast</c>) is caught up from its parent's height (the commitment's height, our
    /// broadcast's confirmation, else the commitment's height); a watch whose spend the chain monitor recorded while the
    /// row stayed unresolved (a crash after the block's save) from that spend's height. Returns false when the scan did
    /// not reach bitcoind's tip (it is tried again in the next round).
    /// </summary>
    private async Task<bool> CatchUpSavedWatchesCoreAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        var watches = new List<(WatchedOutpointModel Watch, uint FromHeight)>();
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
            {
                if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
                 || channel.State != ChannelState.OnchainResolving)
                    return true;

                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var close = await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId);
                if (close is null)
                    return true;

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

                    var from = watch.SpentAtHeight
                            ?? await CatchUpFromAsync(unitOfWork, close, null, watch, null);
                    watches.Add((watch, from));
                }
            }
        }

        if (watches.Count > 0)
            _logger.LogInformation("Checking {Count} saved resolution watches of channel {ChannelId} for spends "
                                 + "mined before they were tracked", watches.Count, channelId);

        return await CatchUpSpendsAsync(channelId, watches, cancellationToken);
    }

    /// <summary>
    /// Scans the blocks from each watch's lower bound up to bitcoind's tip for a spend of a tracked watch that the
    /// chain monitor may have processed before the watch was tracked; a spend found is handled as the monitor's event
    /// would be and recorded on the watch in the same save. Harmless when the monitor raises it too (idempotent).
    /// Returns false when the scan stopped before the tip (the chain could not be read).
    /// </summary>
    private async Task<bool> CatchUpSpendsAsync(ChannelId channelId,
                                          IReadOnlyList<(WatchedOutpointModel Watch, uint FromHeight)> watches,
                                          CancellationToken cancellationToken)
    {
        if (watches.Count == 0)
            return true;

        IBitcoinChainService? chain;
        using (var scope = _serviceScopeFactory.CreateScope())
            chain = scope.ServiceProvider.GetService<IBitcoinChainService>();
        if (chain is null)
            return true;

        var remaining = new Dictionary<OutPoint, uint>();
        foreach (var (watch, from) in watches)
        {
            var outPoint = new OutPoint(new uint256(watch.TransactionId), watch.OutputIndex);
            remaining[outPoint] = remaining.TryGetValue(outPoint, out var known) ? Math.Min(known, from) : from;
        }

        uint tip;
        try
        {
            tip = await chain.GetCurrentBlockHeightAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not check the new watches of channel {ChannelId} for spends already mined",
                             channelId);
            return false;
        }

        for (var height = remaining.Values.Min(); height <= tip && remaining.Count > 0; height++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Block? block;
            try
            {
                block = await chain.GetBlockAsync(height);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Could not read block {Height} to check the new watches of channel {ChannelId}",
                                 height, channelId);
                return false;
            }

            if (block is null)
                return false;

            var blockHash = new Hash(block.GetHash().ToBytes());
            for (var index = 0; index < block.Transactions.Count && remaining.Count > 0; index++)
            {
                var transaction = block.Transactions[index];
                foreach (var input in transaction.Inputs)
                {
                    if (!remaining.TryGetValue(input.PrevOut, out var from) || height < from)
                        continue;

                    remaining.Remove(input.PrevOut);
                    var spendingTxId = new TxId(transaction.GetHash().ToBytes());
                    _logger.LogWarning("Resolution output {Outpoint} of channel {ChannelId} was spent by {TxId} at "
                                     + "height {Height} before its watch was tracked; handling it now",
                                       input.PrevOut, channelId, Display(spendingTxId), height);
                    var args = new OutpointSpentEventArgs(channelId,
                                                          new SignedTransaction(spendingTxId, transaction.ToBytes()),
                                                          height, (uint)index,
                                                          new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N,
                                                          blockHash);
                    await RunChannelAsync(channelId, height, args, true, cancellationToken);
                }
            }
        }

        return true;
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
                _logger.LogWarning("Channel {ChannelId} is resolving on chain without a recorded funding spend",
                                   channelId);
                return;
            }

            var outputs = (await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channelId))
                         .ToDictionary(o => (o.TransactionId, o.OutputIndex));
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
                                                                   revived, cancellationToken);
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
                });
            }

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
        List<OutputResolverAction> actions, List<TxId> revived, CancellationToken cancellationToken)
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
                                                        channelId, height, commitmentNumber: signed.CommitmentNumber)));
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

        var applied = await StageAndSaveAsync(unitOfWork, actions, null, cancellationToken);
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
                // abandoned when its commitment left it, which confirmed later: rebuilt, it has the same txid)
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
            _logger.LogCritical("[{RequirementId}] Channel {ChannelId}: {Alert}", alert.RequirementId, channelId,
                                alert.Message);
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

    private sealed record Applied(
        bool Saved,
        IReadOnlyList<BroadcastTransactionModel> ToPublish,
        IReadOnlyList<WatchedOutpointModel> NewWatches,
        IReadOnlyList<Domain.Channels.Commitments.Events.IChannelDomainEvent> Events,
        IReadOnlyList<AlertAction> Alerts);
}