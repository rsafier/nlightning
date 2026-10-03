using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// The dual-funded open's part of <c>channel_reestablish</c> (BOLT 2 "Message Retransmission"; splicing plan wave DF,
/// DF2, folded into the reestablish planner in SP2-A): the planner decides from the stored interactive-tx rows what the
/// peer's <c>next_funding</c> asks for (the same path as a splice); this loads the open's negotiation (after a restart
/// the interactive-tx driver has forgotten it) and rebuilds its <c>commitment_signed</c>. Scoped; called by the
/// reestablish handler under the channel's lock.
/// </summary>
public sealed class DualFundReestablish
{
    private readonly ILogger<DualFundReestablish> _logger;
    private readonly DualFundedOpenService _service;
    private readonly IUnitOfWork _unitOfWork;

    public DualFundReestablish(ILogger<DualFundReestablish> logger, DualFundedOpenService service,
                               IUnitOfWork unitOfWork)
    {
        _logger = logger;
        _service = service;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Whether <paramref name="channel"/> is a dual-funded open waiting for its funding transaction.</summary>
    public static bool IsPendingOpen(ChannelModel channel) =>
        channel is { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned };

    /// <summary>
    /// Loads the open's negotiation and resumes the interactive-tx driver on it when it is not in memory (after a
    /// restart), so the peer's retransmitted <c>tx_signatures</c> and our own rebuilt ones find it. Nothing for a
    /// channel that is not a pending dual-funded open.
    /// </summary>
    public async Task EnsureLoadedAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (IsPendingOpen(channel))
            await _service.GetOrLoadAsync(channel.ChannelId, _unitOfWork, CancellationToken.None);
    }

    /// <summary>
    /// Abandons the open's RBF attempt that is not signed when an earlier attempt confirmed (NL-867), before the
    /// reestablish is planned: the peer's <c>next_funding</c> for it then gets our <c>tx_abort</c> (returned) instead of
    /// its <c>commitment_signed</c> again. Empty when nothing was abandoned (or the channel is no pending open).
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> AbandonConfirmedRbfAttemptAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return IsPendingOpen(channel) ? await _service.AbandonRbfAttemptIfConfirmedAsync(channel, _unitOfWork) : [];
    }

    /// <summary>
    /// Our <c>commitment_signed</c> for the open's funding transaction <paramref name="fundingTxId"/> again (RFC 6979:
    /// the same signature as the first time), when it is the negotiation's pending transaction; null otherwise.
    /// </summary>
    public async Task<CommitmentSignedMessage?> CreateCommitmentSignedRetransmissionAsync(ChannelModel channel,
                                                                                        TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsPendingOpen(channel))
            return null;

        var negotiation = await _service.GetOrLoadAsync(channel.ChannelId, _unitOfWork, CancellationToken.None);
        if (negotiation?.PendingTxId is not { } pending || !pending.Equals(fundingTxId))
        {
            _logger.LogInformation("No pending dual-funded transaction {TxId} on channel {ChannelId} to sign again",
                                   fundingTxId, channel.ChannelId);
            return null;
        }

        return _service.CreateCommitmentSignedRetransmission(negotiation);
    }
}