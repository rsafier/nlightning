using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Events;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Sends <c>splice_locked</c> when a pending splice transaction reaches acceptable depth (BOLT 2 SP-LK-01; D8: the
/// channel's <c>minimum_depth</c>): the completion of a splice watches its transaction with that depth
/// (<c>WatchedTransactions</c>), and the chain monitor's <see cref="IBlockchainMonitor.OnTransactionConfirmed"/> for a
/// pending splice of the channel hands it, with its block and index (the splice's short channel id), to
/// <see cref="SpliceService.OnSpliceDepthReachedAsync"/>, off the event thread; <see cref="CatchUpAsync"/> does the same
/// at startup for splices confirmed while nothing listened. The announcement depth of a locked splice needs no watch:
/// the lock gives the channel the splice's short channel id, and the channel manager's block-driven announcement round
/// sends <c>announcement_signatures</c> at its 6th confirmation (SP-G-01). Reorgs of a splice are SP2-C-T4: one before
/// the lock moves the pending splice back to waiting (NL-493), one after it is the resolution executor's
/// <c>SpliceReorgMonitor</c> alert.
/// </summary>
/// <remarks>
/// Singleton; subscribes in its constructor. <see cref="SpliceService"/> resolves it in its own constructor (and it
/// resolves the service lazily), so it runs from the first splice message or operator splice of the process on. The
/// host resolves it and calls <see cref="CatchUpAsync"/> at startup, after the chain monitor and the peer manager
/// started: a splice whose watch completed while nothing listened (before the first splice message of the process)
/// is then handed over too. The channel manager also receives the event (a funding confirmation of a channel still
/// being opened); it ignores an <c>Open</c> channel, and the reorg handler of the funding compares the txid with the
/// channel's funding.
/// </remarks>
public sealed class SpliceDepthWatcher : ISpliceDepthWatcher, IDisposable
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<SpliceDepthWatcher> _logger;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ISpliceStatePort _statePort;

    public SpliceDepthWatcher(IBlockchainMonitor blockchainMonitor, IChannelMemoryRepository channelMemoryRepository,
                              IServiceProvider serviceProvider, ISpliceStatePort statePort,
                              ILogger<SpliceDepthWatcher> logger)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _serviceProvider = serviceProvider;
        _statePort = statePort;
        _logger = logger;
        _blockchainMonitor.OnTransactionConfirmed += OnTransactionConfirmed;
        _blockchainMonitor.OnBlockDisconnected += OnBlockDisconnected;
    }

    /// <summary>Completes when no confirmation handed over by this watcher is still being handled (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys.ToArray());
    }

    /// <summary>
    /// Hands over every pending splice of a loaded channel for which we have not sent <c>splice_locked</c> and whose
    /// watch already completed (its confirmation was raised while nothing listened, e.g. before the first splice
    /// message after a restart), and moves every pending splice whose depth we had reached before a restart back to
    /// waiting when the chain monitor's reorg rollback cleared its watch row's position (SP2-C-T4, NL-493).
    /// <see cref="SpliceService.OnSpliceDepthReachedAsync"/> is idempotent, so a splice the event handles at the same
    /// time is locked once. Returns how many were handed over.
    /// </summary>
    public async Task<int> CatchUpAsync(CancellationToken cancellationToken = default)
    {
        var due = new List<(ChannelId ChannelId, TxId TxId)>();
        var reorged = new List<(ChannelId ChannelId, TxId TxId)>();
        foreach (var channel in _channelMemoryRepository.FindChannels(_ => true))
        {
            try
            {
                var pending = _statePort.GetFundings(channel).Pending;
                due.AddRange(pending.Where(f => !f.SpliceLockedSent)
                                    .Select(f => (channel.ChannelId, f.FundingTxId)));
                reorged.AddRange(pending.Where(f => f.SpliceLockedSent)
                                        .Select(f => (channel.ChannelId, f.FundingTxId)));
            }
            catch (InvalidOperationException)
            {
                // A channel without a funding outpoint has no splice
            }
        }

        using var scope = _serviceProvider.CreateScope();
        var watches = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WatchedTransactionDbRepository;

        if (due.Count == 0 && reorged.Count == 0)
            return 0;

        // A depth we reached needs no hand-over, but its watch row tells whether its block is still in the active
        // chain: the rollback of a reorg clears the position, and the splice must wait for its depth again
        foreach (var (channelId, txId) in reorged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await watches.GetByTransactionIdAsync(txId) is not { FirstSeenAtHeight: null } watch
             || watch.ChannelId != channelId)
                continue;

            _logger.LogInformation("Splice {TxId} of channel {ChannelId} had reached its depth before the restart and "
                                 + "left the active chain in a reorg; it waits for its depth again", txId, channelId);
            await _serviceProvider.GetRequiredService<SpliceService>()
                                  .OnSpliceReorgedOutAsync(channelId, txId, 0, cancellationToken);
        }

        var handed = 0;
        foreach (var (channelId, txId) in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await watches.GetByTransactionIdAsync(txId) is not { IsCompleted: true } watch
             || watch.ChannelId != channelId || watch.FirstSeenAtHeight is not { } height)
                continue;

            _logger.LogInformation("Splice {TxId} of channel {ChannelId} reached its depth while nothing listened; "
                                 + "locking it now", txId, channelId);
            await HandleAsync(channelId, txId, height, watch.TransactionIndex);
            handed++;
        }

        return handed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _blockchainMonitor.OnTransactionConfirmed -= OnTransactionConfirmed;
        _blockchainMonitor.OnBlockDisconnected -= OnBlockDisconnected;
    }

    private void OnTransactionConfirmed(object? sender, TransactionConfirmedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var watch = args.WatchedTransaction;
        try
        {
            if (!_channelMemoryRepository.TryGetChannel(watch.ChannelId, out var channel)
             || _statePort.GetFundings(channel).Pending.All(f => f.FundingTxId != watch.TransactionId))
                return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Track(() => HandleAsync(watch.ChannelId, watch.TransactionId,
                                watch.FirstSeenAtHeight ?? args.Height, watch.TransactionIndex));
    }

    /// <summary>
    /// SP2-C-T4, before the lock (NL-493): a pending splice whose confirming block was disconnected is unconfirmed
    /// again. Its depth state (our <c>splice_locked</c> sent with the short channel id of the lost block) must be
    /// reset, or the re-confirmation would be swallowed by the lock's idempotency with the old short channel id kept:
    /// the splice goes back to waiting for its depth, and the lock re-runs with the new block's when it confirms again.
    /// </summary>
    private void OnBlockDisconnected(object? sender, BlockDisconnectedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var reorged = new List<(ChannelId ChannelId, TxId TxId)>();
        foreach (var channel in _channelMemoryRepository.FindChannels(_ => true))
        {
            try
            {
                reorged.AddRange(_statePort.GetFundings(channel).Pending
                                 .Where(f => f.ConfirmedHeight is { } confirmed && confirmed > args.ForkHeight)
                                 .Select(f => (channel.ChannelId, f.FundingTxId)));
            }
            catch (InvalidOperationException)
            {
                // A channel without a funding outpoint has no splice
            }
        }

        foreach (var (channelId, txId) in reorged)
            Track(() => HandleReorgedOutAsync(channelId, txId, args.ForkHeight));
    }

    private void Track(Func<Task> round)
    {
        Task task;
        using (ExecutionContext.SuppressFlow())
            task = Task.Run(round);
        _running[task] = 0;
        task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
                          TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (task.IsCompleted)
            _running.TryRemove(task, out _);
    }

    private async Task HandleAsync(ChannelId channelId, TxId txId, uint height, uint? transactionIndex)
    {
        try
        {
            await _serviceProvider.GetRequiredService<SpliceService>()
                                  .OnSpliceDepthReachedAsync(channelId, txId, height, transactionIndex);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not lock splice {TxId} of channel {ChannelId}", txId, channelId);
        }
    }

    private async Task HandleReorgedOutAsync(ChannelId channelId, TxId txId, uint forkHeight)
    {
        try
        {
            await _serviceProvider.GetRequiredService<SpliceService>()
                                  .OnSpliceReorgedOutAsync(channelId, txId, forkHeight);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not move the reorged-out splice {TxId} of channel {ChannelId} back to waiting",
                             txId, channelId);
        }
    }
}