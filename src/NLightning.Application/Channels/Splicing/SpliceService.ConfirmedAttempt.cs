using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Interfaces;

/// <summary>
/// NL-867, splice RBF: once a pending attempt of the splice has a confirmation, every other attempt double-spends it
/// and can never confirm (BOLT 2: "If the previous transaction confirms in the middle of an RBF attempt, the attempt
/// MUST be abandoned"). No RBF is started or accepted then (<see cref="SpliceRbfConditions.AttemptConfirmed"/>); an
/// RBF attempt already running is abandoned with our <c>tx_abort</c> at the block that confirmed its sibling
/// (<see cref="ScheduleConfirmedAttemptRound"/>), on <c>channel_reestablish</c>
/// (<see cref="AbandonConfirmedRbfAttemptAsync"/>) and before our <c>tx_signatures</c>
/// (<see cref="GetTxSignaturesRefusalAsync"/>), never after them (IT-ABT-01).
/// </summary>
public sealed partial class SpliceService
{
    /// <summary>
    /// A block was processed (called by <c>ChannelManager</c> for every block): every running splice RBF attempt (our
    /// <c>tx_init_rbf</c> waiting for its answer, or the negotiation before our <c>tx_signatures</c>) whose pending
    /// sibling now has a confirmation is abandoned, each under its channel's lock and off the caller's thread.
    /// </summary>
    public void ScheduleConfirmedAttemptRound()
    {
        foreach (var negotiation in _negotiations.Values.ToList())
        {
            if (negotiation is
                {
                    Model.IsRbf: true,
                    State: SpliceNegotiationState.InitSent or SpliceNegotiationState.Negotiating
                        or SpliceNegotiationState.CommitmentSigned
                })
                TrackBackground(AbandonConfirmedRbfAttemptRoundAsync(negotiation));
        }
    }

    /// <summary>
    /// <c>channel_reestablish</c> (called by the reestablish handler under the channel's lock, before it plans): the
    /// channel's splice RBF attempt that is not signed, resumed from its rows when needed, is abandoned when a pending
    /// sibling has a confirmation, so the peer's <c>next_funding</c> for it gets our <c>tx_abort</c> (returned) instead
    /// of its <c>commitment_signed</c> again. Empty when nothing was abandoned.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> AbandonConfirmedRbfAttemptAsync(ChannelModel channel,
                                                                                     IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        await EnsureLoadedAsync(channel, unitOfWork);
        return Get(channel.ChannelId) is { Model.IsRbf: true } negotiation
                   ? await AbandonConfirmedRbfAttemptLockedAsync(negotiation, unitOfWork)
                   : [];
    }

    /// <summary>
    /// The splice host's check before our <c>tx_signatures</c>: an RBF attempt whose pending sibling has a confirmation
    /// is abandoned instead of signed. Null for a splice that is no RBF, or while no sibling has a confirmation.
    /// </summary>
    internal async Task<string?> GetTxSignaturesRefusalAsync(SpliceNegotiation negotiation)
    {
        if (!negotiation.Model.IsRbf || !_channelMemoryRepository.TryGetChannel(negotiation.ChannelId, out var channel))
            return null;

        return await GetConfirmedPendingAttemptAsync(_statePort.GetFundings(channel), GetAttemptTxId(negotiation),
                                                     null) is { } confirmed
                   ? EarlierAttemptConfirmed(confirmed)
                   : null;
    }

    /// <summary>
    /// A pending attempt of <paramref name="fundings"/> other than <paramref name="exceptTxId"/> that has a
    /// confirmation: its depth reached (<see cref="ChannelFunding.ConfirmedHeight"/>), or its watch's first-seen height
    /// set by the chain monitor. Null when none has.
    /// </summary>
    private async Task<TxId?> GetConfirmedPendingAttemptAsync(FundingSet fundings, TxId? exceptTxId,
                                                              IUnitOfWork? unitOfWork)
    {
        var siblings = fundings.Pending.Where(f => f.FundingTxId != exceptTxId).ToList();
        if (siblings.FirstOrDefault(f => f.ConfirmedHeight is not null) is { } atDepth)
            return atDepth.FundingTxId;
        if (siblings.Count == 0)
            return null;

        using var scope = unitOfWork is null ? _serviceProvider.CreateScope() : null;
        var watches = (unitOfWork ?? scope!.ServiceProvider.GetRequiredService<IUnitOfWork>())
           .WatchedTransactionDbRepository;
        foreach (var sibling in siblings)
        {
            if (await watches.GetByTransactionIdAsync(sibling.FundingTxId) is { FirstSeenAtHeight: not null })
                return sibling.FundingTxId;
        }

        return null;
    }

    private static string EarlierAttemptConfirmed(TxId confirmed) => $"an earlier attempt {confirmed} confirmed";

    /// <summary>The RBF attempt's own transaction, once constructed (its funding is not a sibling).</summary>
    private static TxId? GetAttemptTxId(SpliceNegotiation negotiation) =>
        negotiation.NewFunding?.FundingTxId ?? negotiation.Model.SpliceTxId;

    private async Task AbandonConfirmedRbfAttemptRoundAsync(SpliceNegotiation negotiation)
    {
        try
        {
            await Task.Yield();
            using var channelLock = await _channelLockProvider.AcquireAsync(negotiation.ChannelId);
            if (!ReferenceEquals(Get(negotiation.ChannelId), negotiation))
                return;

            var messages = await AbandonConfirmedRbfAttemptLockedAsync(negotiation, null);
            if (messages.Count > 0)
                GetPublisher()?.Publish(negotiation.PeerPubKey, messages);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not abandon the splice RBF attempt of channel {ChannelId}",
                             negotiation.ChannelId);
        }
    }

    /// <summary>
    /// Under the channel's lock: the RBF attempt is abandoned when a pending sibling has a confirmation; returns our
    /// <c>tx_abort</c> (to send), empty when nothing was abandoned (no confirmation, or our <c>tx_signatures</c> went
    /// out: IT-ABT-01).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> AbandonConfirmedRbfAttemptLockedAsync(
        SpliceNegotiation negotiation, IUnitOfWork? unitOfWork)
    {
        var channelId = negotiation.ChannelId;
        var driver = GetDriver();
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || driver.GetInfo(channelId) is not { } info || info is { SessionId: null, RbfRequested: false }
         || info.State is InteractiveTxSessionState.TxSignaturesSent or InteractiveTxSessionState.Signed
         || await GetConfirmedPendingAttemptAsync(_statePort.GetFundings(channel), GetAttemptTxId(negotiation),
                                                  unitOfWork) is not { } confirmed)
            return [];

        var reason = EarlierAttemptConfirmed(confirmed);
        _logger.LogInformation("Abandoning the splice RBF attempt of channel {ChannelId}: {Reason}", channelId,
                               reason);
        try
        {
            using var scope = unitOfWork is null ? _serviceProvider.CreateScope() : null;
            var messages = await driver.AbortAsync(channelId, reason,
                                                   unitOfWork ?? scope!.ServiceProvider
                                                                      .GetRequiredService<IUnitOfWork>());
            if (info.SessionId is null)
            {
                // Our tx_init_rbf was withdrawn before any attempt existed: the negotiation and the quiescence end
                // here (an attempt's abort ends both through the host and the driver)
                _serviceProvider.GetService<IQuiescenceService>()?.Terminate(channelId, QuiescenceEndReason.TxAbort);
                await EndBeforeNegotiationAsync(negotiation, reason);
            }

            return messages;
        }
        catch (InvalidOperationException e)
        {
            // Our tx_signatures went out meanwhile (IT-ABT-01)
            _logger.LogWarning(e, "Could not abandon the splice RBF attempt of channel {ChannelId}", channelId);
            return [];
        }
    }
}