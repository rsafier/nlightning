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

            var (due, waiting) = await FindDueAsync(channel, height);
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
    /// the winning transaction spent; <c>waiting</c> when another one's conflict is not deep enough yet.
    /// </summary>
    private async Task<(List<(InteractiveTxSessionModel Session, List<(TxId, uint)> Kept)> Due, bool Waiting)>
        FindDueAsync(ChannelModel channel, uint height)
    {
        var due = new List<(InteractiveTxSessionModel, List<(TxId, uint)>)>();
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
        var discarded = fundings.Where(f => f.Status == ChannelFundingStatus.Discarded)
                                .Select(f => f.FundingTxId)
                                .ToHashSet();
        if (discarded.Count == 0)
            return (due, false);

        IReadOnlyList<InteractiveTxSessionModel> sessions;
        try
        {
            sessions = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return (due, false);
        }

        var waiting = false;
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

        return (due, waiting);
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