using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
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

    /// <summary>
    /// Scans the blocks from each watch's lower bound up to bitcoind's tip for a spend of a tracked watch that the
    /// chain monitor may have processed before the watch was tracked; a spend found is handled as the monitor's event
    /// would be and recorded on the watch in the same save. Harmless when the monitor raises it too (idempotent).
    /// </summary>
    private async Task CatchUpSpendsAsync(ChannelId channelId,
                                          IReadOnlyList<(WatchedOutpointModel Watch, uint FromHeight)> watches,
                                          CancellationToken cancellationToken)
    {
        if (watches.Count == 0)
            return;

        IBitcoinChainService? chain;
        using (var scope = _serviceScopeFactory.CreateScope())
            chain = scope.ServiceProvider.GetService<IBitcoinChainService>();
        if (chain is null)
            return;

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
            return;
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
                return;
            }

            if (block is null)
                return;

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
            var resolver = GetResolver(scope, close.Kind);

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
        }

        await AfterSaveAsync(scope, channelId, applied);
        await CatchUpSpendsAsync(channelId, catchUp, cancellationToken);
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