using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Returns the wallet inputs of a discarded splice to the wallet once the transaction that conflicts with it is
/// irrevocable (NL-492, splicing plan §3.6): a commitment of the funding it spends (the close's save marks the splice
/// <see cref="ChannelFundingStatus.Discarded"/>) or another splice of that funding (an RBF sibling that locked).
/// </summary>
/// <remarks>
/// <para>The conflict is read from the chain monitor's record of the funding the discarded splice spends (its shared
/// input): a spend by another transaction <see cref="Onchain.OnchainOptions.IrrevocableDepth"/> blocks deep. Before
/// that nothing is released: a reorg could still bring the discarded splice back (the watcher sets it
/// <see cref="ChannelFundingStatus.Pending"/> again when it confirms instead of the close), and the reservation must
/// still hold its inputs then (IT-ABT-01). So a reorg never has a released reservation to roll back.</para>
/// <para>Order: <see cref="IInteractiveTxContributor.ReleaseDiscardedAsync"/> returns the outputs (kept are the inputs
/// of the winning splice, when it is one of ours), then, once no reservation holds our other inputs any more, the
/// negotiation is marked settled (<see cref="InteractiveTxSessionModel.ResolvedAt"/>) under the channel's lock in one
/// save. While the contributor keeps a reservation (a kept outpoint the wallet has not dropped yet, or another input),
/// the negotiation stays unresolved and the release is tried again on the next block; the startup sweep does not
/// release a reservation an unresolved negotiation holds, so this round is what frees it. A crash between the release
/// and the save repeats the (idempotent) release on the next round.</para>
/// <para>Run by the resolution executor at the start of every block round (before a channel may reach Closed and leave
/// memory). A channel is read again only when its state or current funding changed, or while it has a discarded splice
/// waiting for its conflict's depth.</para>
/// <para>The same holds for the attempts of a dual-funded open's RBF that lost to the one that confirmed (NL-528): an
/// attempt can add wallet inputs the others do not have (an accepter that funded nothing at the open contributes in
/// the RBF), and those stay reserved while the losing attempt is unresolved. Its conflict is the channel's confirmed
/// funding transaction (<see cref="ChannelModel.FundingCreatedAtBlockHeight"/> is its height once the confirmation
/// was applied); the kept outpoints are the inputs of the confirmed attempt. In the same round the losing attempts'
/// funding watches are removed (NL-529): a watch of their funding transaction (a row and the monitor's memory) and of
/// their funding outpoint (a row and the monitor's memory) can never complete any more, and the chain monitor would
/// load both again on every start. The rows go in one save per channel under the channel's lock; a failed removal
/// leaves the round waiting, so it is tried again on the next block.</para>
/// </remarks>
internal sealed class DiscardedSpliceReservations
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ConcurrentDictionary<ChannelId, (ChannelState State, TxId Current)> _settled = new();
    private readonly uint _irrevocableDepth;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public DiscardedSpliceReservations(IChannelLockProvider channelLockProvider,
                                       IChannelMemoryRepository channelMemoryRepository, ILogger logger,
                                       IServiceScopeFactory serviceScopeFactory, uint irrevocableDepth,
                                       TimeProvider? timeProvider = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _irrevocableDepth = irrevocableDepth;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Releases, at <paramref name="height"/>, what every loaded channel's discarded splices reserved once
    /// their conflict is irrevocable; returns how many wallet outputs were released.</summary>
    public async Task<int> CheckAsync(uint height, CancellationToken cancellationToken)
    {
        var channels = _channelMemoryRepository.FindChannels(
            c => c.State is not (ChannelState.Closed or ChannelState.Stale)
              && c.FundingOutput?.TransactionId is not null);
        if (channels is not { Count: > 0 })
            return 0;

        var released = 0;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (channel.State, channel.FundingOutput!.TransactionId!.Value);
            if (_settled.TryGetValue(channel.ChannelId, out var known) && known == key)
                continue;

            var (due, lost, waiting) = await FindDueAsync(channel, height);
            if (lost.Count > 0)
                waiting |= !await RemoveFundingWatchesAsync(channel, lost, cancellationToken);
            if (!waiting && due.Count == 0)
                _settled[channel.ChannelId] = key;
            else
                _settled.TryRemove(channel.ChannelId, out _);

            foreach (var (session, kept) in due)
                released += await ReleaseAsync(channel.ChannelId, session, kept, cancellationToken);
        }

        return released;
    }

    /// <summary>
    /// The unresolved negotiations of the channel's discarded splices whose conflict is irrevocable, with the outpoints
    /// the winning transaction spent, and the losing attempts of a confirmed dual-funded open whose funding watches
    /// still exist (NL-529); <c>waiting</c> when another one's conflict is not deep enough yet.
    /// </summary>
    private async Task<(List<(InteractiveTxSessionModel Session, List<(TxId, uint)> Kept)> due,
                        List<(InteractiveTxSessionModel Session, TxId FundingTxId, uint SharedOutputIndex)> lost,
                        bool waiting)>
        FindDueAsync(ChannelModel channel, uint height)
    {
        var due = new List<(InteractiveTxSessionModel, List<(TxId, uint)>)>();
        var lost = new List<(InteractiveTxSessionModel, TxId, uint)>();
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
        var discarded = fundings.Where(f => f.Status == ChannelFundingStatus.Discarded)
                                .Select(f => f.FundingTxId)
                                .ToHashSet();
        var confirmedOpen = GetConfirmedDualFundedOpen(channel);
        if (discarded.Count == 0 && confirmedOpen is null)
            return (due, lost, false);

        IReadOnlyList<InteractiveTxSessionModel> sessions;
        try
        {
            sessions = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return (due, lost, false);
        }

        var waiting = false;
        if (confirmedOpen is { } open)
        {
            var (attempts, stillWaiting) = FindLosingOpenAttempts(sessions, due, open.FundingTxId, open.Height,
                                                                  height);
            waiting = stillWaiting;
            lost.AddRange(await FindLosingWatchesAsync(unitOfWork, attempts));
        }

        foreach (var session in sessions)
        {
            if (session is not
                {
                    ResolvedAt: null, ConstructedTx: { } constructed
                }
             || session.State == InteractiveTxSessionState.Aborted
             || !discarded.Contains(constructed.TxId)
             || !constructed.Inputs.Any(i => i is { AddedBy: InteractiveTxParty.Local, IsShared: false }))
                continue;

            if (constructed.Inputs.FirstOrDefault(i => i.IsShared) is not { } shared)
                continue;

            var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(shared.PrevTxId, shared.PrevTxVout);
            if (watch is not { SpentAtHeight: { } spentAt, SpentByTransactionId: { } spentBy }
             || spentBy == constructed.TxId)
            {
                // The funding it spends is unspent (a reorg) or the chain monitor has not recorded the conflict yet
                waiting = true;
                continue;
            }

            if (Depth(height, spentAt) < _irrevocableDepth)
            {
                waiting = true;
                continue;
            }

            // Outpoints the winning transaction spent (an input a locked RBF sibling re-added) stay reserved
            var kept = sessions.FirstOrDefault(s => s.ConstructedTx?.TxId == spentBy)?.ConstructedTx!.Inputs
                               .Select(i => (i.PrevTxId, i.PrevTxVout))
                               .ToList()
                    ?? [];
            due.Add((session, kept));
        }

        return (due, lost, waiting);
    }

    /// <summary>
    /// The confirmed funding of a dual-funded open whose confirmation was applied (NL-528), or null: a v2 channel past
    /// <see cref="ChannelState.V1FundingSigned"/> on its initial funding, with the height it confirmed at.
    /// </summary>
    private static (TxId FundingTxId, uint Height)? GetConfirmedDualFundedOpen(ChannelModel channel) =>
        channel is
        {
            Version: ChannelVersion.V2, State: > ChannelState.V1FundingSigned, LocalFundingKeyIndex: 0,
            FundingCreatedAtBlockHeight: > 0, FundingOutput.TransactionId: { } fundingTxId
        }
            ? (fundingTxId, channel.FundingCreatedAtBlockHeight)
            : null;

    /// <summary>The losing attempts of the dual-funded open at <paramref name="height"/>, and whether one of them
    /// still waits for the confirmed funding to become irrevocable (NL-528). An attempt that holds wallet inputs of
    /// ours is queued for the release of its reservation (NL-492, <paramref name="due"/>); every losing attempt's
    /// funding watches are removed once the depth is reached (NL-529).</summary>
    /// <param name="attempts">Every fully negotiated attempt of the open that is not the confirmed funding, whether
    /// their negotiation is settled or not: their watches go at the same depth as their reservations (NL-529).</param>
    private (List<(InteractiveTxSessionModel Session, TxId FundingTxId, uint SharedOutputIndex)> Attempts,
             bool Waiting)
        FindLosingOpenAttempts(IReadOnlyList<InteractiveTxSessionModel> sessions,
                               List<(InteractiveTxSessionModel Session, List<(TxId, uint)> Kept)> due,
                               TxId confirmedTxId, uint confirmedHeight, uint height)
    {
        var attempts = new List<(InteractiveTxSessionModel, TxId, uint)>();
        var kept = sessions.FirstOrDefault(s => s.ConstructedTx?.TxId == confirmedTxId)?.ConstructedTx!.Inputs
                           .Select(i => (i.PrevTxId, i.PrevTxVout))
                           .ToList()
                ?? [];
        var waiting = false;
        foreach (var session in sessions)
        {
            if (session is not { ConstructedTx: { } constructed }
             || session.State == InteractiveTxSessionState.Aborted
             || session.Purpose is not (InteractiveTxPurpose.DualFund or InteractiveTxPurpose.DualFundRbf)
             || constructed.TxId == confirmedTxId
             || constructed.SharedOutputIndex is not { } sharedOutputIndex)
                continue;

            if (Depth(height, confirmedHeight) < _irrevocableDepth)
            {
                waiting = true;
                continue;
            }

            attempts.Add((session, constructed.TxId, sharedOutputIndex));
            if (session.ResolvedAt is null
             && constructed.Inputs.Any(i => i is { AddedBy: InteractiveTxParty.Local, IsShared: false }))
                due.Add((session, kept));
        }

        return (attempts, waiting);
    }

    /// <summary>The losing attempts whose funding watches still exist: a pending watched transaction, an unspent
    /// watched funding outpoint, or both (NL-529).</summary>
    private async Task<List<(InteractiveTxSessionModel Session, TxId FundingTxId, uint SharedOutputIndex)>>
        FindLosingWatchesAsync(IUnitOfWork unitOfWork,
                               List<(InteractiveTxSessionModel Session, TxId FundingTxId, uint SharedOutputIndex)>
                                   attempts)
    {
        var lost = new List<(InteractiveTxSessionModel, TxId, uint)>();
        try
        {
            var transactions = unitOfWork.WatchedTransactionDbRepository;
            var outpoints = unitOfWork.WatchedOutpointDbRepository;
            if (transactions is null || outpoints is null)
                return lost;

            foreach (var (session, txId, sharedOutputIndex) in attempts)
            {
                if (await transactions.GetByTransactionIdAsync(txId) is { IsCompleted: false })
                {
                    lost.Add((session, txId, sharedOutputIndex));
                    continue;
                }

                if (await outpoints.GetAsync(txId, sharedOutputIndex) is { SpentAtHeight: null })
                    lost.Add((session, txId, sharedOutputIndex));
            }
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            // A unit of work without watch rows keeps none: the releases of the reservations still go
        }

        return lost;
    }

    /// <summary>
    /// Removes, under the channel's lock, the funding watches of the open's losing attempts once the confirmed funding
    /// is irrevocable (NL-529): the watched transaction (deleted from <c>WatchedTransactions</c>, untracked in the
    /// chain monitor) and the watched funding outpoint (deleted from <c>WatchedOutpoints</c>, unwatched). They could
    /// never confirm any more: their shared input was spent by a transaction that can no longer be disconnected. False
    /// when a removal failed, so the round tries again on the next block; a removal is idempotent.
    /// </summary>
    private async Task<bool> RemoveFundingWatchesAsync(ChannelModel channel,
        List<(InteractiveTxSessionModel Session, TxId FundingTxId, uint SharedOutputIndex)> lost,
        CancellationToken cancellationToken)
    {
        var removedAll = true;
        foreach (var (_, txId, sharedOutputIndex) in lost)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                using (await _channelLockProvider.AcquireAsync(channel.ChannelId, cancellationToken))
                {
                    var removedTransaction =
                        await unitOfWork.WatchedTransactionDbRepository.DeleteByTransactionIdAsync(txId);
                    var removedOutpoint =
                        await unitOfWork.WatchedOutpointDbRepository.DeleteByTransactionIdAsync(txId,
                                                                                                sharedOutputIndex);
                    if (removedTransaction || removedOutpoint)
                        await unitOfWork.SaveChangesAsync();
                }

                if (scope.ServiceProvider.GetService<IBlockchainMonitor>() is { } monitor)
                {
                    monitor.StopWatchingTransaction(txId);
                    monitor.StopWatchingOutpointSpend(txId, sharedOutputIndex);
                }

                _logger.LogInformation(
                    "Channel {ChannelId}: the funding watches of the losing attempt {TxId} are removed (the funding "
                  + "that confirmed is irrevocable, NL-529)", channel.ChannelId, txId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                removedAll = false;
                _logger.LogError(e, "Removing the funding watches of losing attempt {TxId} of channel {ChannelId} "
                                  + "failed", txId, channel.ChannelId);
            }
        }

        return removedAll;
    }

    private async Task<int> ReleaseAsync(ChannelId channelId, InteractiveTxSessionModel session,
                                         List<(TxId, uint)> kept, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<IInteractiveTxContributor>() is not { } contributor)
            return 0;

        var discarded = session.ConstructedTx!;
        try
        {
            var released = await contributor.ReleaseDiscardedAsync(discarded, kept, cancellationToken);

            // Settled only once no reservation holds our inputs any more: a reservation the contributor kept (a kept
            // outpoint the wallet still holds, another input) is tried again on the next block (NL-492)
            if (scope.ServiceProvider.GetService<IFeeInputSelector>() is { } selector
             && await IsStillReservedAsync(selector, discarded, kept, cancellationToken))
            {
                _logger.LogInformation("Channel {ChannelId}: {Count} wallet output(s) of discarded splice {TxId} were "
                                     + "released, the rest is still reserved; trying again on the next block",
                                       channelId, released, Display(discarded.TxId));
                return released;
            }

            using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var stored = await unitOfWork.InteractiveTxSessionDbRepository.GetByIdAsync(channelId,
                                                                                            session.SessionId);
                if (stored is null || stored.ResolvedAt is not null)
                    return released;

                await unitOfWork.InteractiveTxSessionDbRepository.UpdateAsync(
                    stored with { ResolvedAt = _timeProvider.GetUtcNow() });
                await unitOfWork.SaveChangesAsync();
            }

            _logger.LogInformation("Channel {ChannelId}: the transaction conflicting with discarded splice {TxId} is "
                                 + "irrevocable; {Count} wallet output(s) it reserved are spendable again (NL-492)",
                                   channelId, Display(discarded.TxId), released);
            return released;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Releasing the wallet inputs of discarded splice {TxId} of channel {ChannelId} failed",
                             Display(discarded.TxId), channelId);
            return 0;
        }
    }

    /// <summary>Whether a reservation still holds one of our wallet inputs of <paramref name="discarded"/> that the
    /// winning transaction did not spend.</summary>
    private static async Task<bool> IsStillReservedAsync(IFeeInputSelector selector, ConstructedInteractiveTx discarded,
                                                         List<(TxId, uint)> kept, CancellationToken cancellationToken)
    {
        var ours = discarded.Inputs.Where(i => i is { AddedBy: InteractiveTxParty.Local, IsShared: false })
                            .Select(i => (i.PrevTxId, i.PrevTxVout))
                            .Where(o => !kept.Contains(o))
                            .ToHashSet();
        if (ours.Count == 0)
            return false;

        var reservations = await selector.GetAllAsync(cancellationToken);
        return reservations.Any(r => r.Inputs.Any(i => ours.Contains((i.TxId, i.Index))));
    }

    private static uint Depth(uint tip, uint height) => tip >= height ? tip - height + 1 : 0;

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();
}