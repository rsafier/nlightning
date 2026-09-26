using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Onchain;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Onchain;
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
        RunChannelAsync(channelId, height, null, cancellationToken);

    /// <inheritdoc />
    public Task HandleOutputSpentAsync(OutpointSpentEventArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.SpentTransactionId is null || args.SpentOutputIndex is null)
            return Task.CompletedTask;

        return RunChannelAsync(args.ChannelId, args.BlockHeight, args, cancellationToken);
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
                                       CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        Applied applied;
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

            // Plan §3.2 step 5: Closed (and the revocation log dropped) once everything is irrevocably resolved
            var closed = Depth(height, close.SpentAtHeight) >= _options.IrrevocableDepth
                      && outputs.Values.All(o => o.State is OutputResolutionState.Irrevocable
                                                           or OutputResolutionState.Ignored);
            Func<Task>? closeChannel = null;
            if (closed)
            {
                closeChannel = async () =>
                {
                    channel.UpdateState(ChannelState.Closed);
                    await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
                    await unitOfWork.RevokedCommitmentDbRepository.DeleteByChannelIdAsync(channelId);
                };
            }

            applied = await StageAndSaveAsync(unitOfWork, actions, closeChannel, cancellationToken);
            if (closed)
            {
                _channelMemoryRepository.TryRemoveChannel(channelId);
                _logger.LogWarning("Channel {ChannelId} is closed: its funding spend and every output are "
                                 + "irrevocably resolved at height {Height}", channelId, height);
            }
        }

        await AfterSaveAsync(scope, channelId, applied);
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

    private static string Display(TxId txId) => new NBitcoin.uint256(txId).ToString();

    private sealed record Applied(
        bool Saved,
        IReadOnlyList<BroadcastTransactionModel> ToPublish,
        IReadOnlyList<WatchedOutpointModel> NewWatches,
        IReadOnlyList<Domain.Channels.Commitments.Events.IChannelDomainEvent> Events,
        IReadOnlyList<AlertAction> Alerts);
}