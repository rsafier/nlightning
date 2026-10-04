using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;

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
    /// Simple taproot opens (BOLTs PR #1324, taproot wave t02 lane V2): the peer's <c>channel_reestablish</c>
    /// <c>current_commit_nonce</c> (type 24, null when absent), which a retransmitted <c>commitment_signed</c> of the open
    /// is signed against (<see cref="CreateCommitmentSignedRetransmissionAsync"/>). Call it after
    /// <see cref="EnsureLoadedAsync"/>; nothing for a channel that is not a pending dual-funded open.
    /// </summary>
    public async Task ReceiveCurrentCommitNonceAsync(ChannelModel channel, MusigPublicNonce? nonce)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsPendingOpen(channel)
         || await _service.GetOrLoadAsync(channel.ChannelId, _unitOfWork, CancellationToken.None) is not
         { } negotiation)
            return;

        _service.ReceiveCurrentCommitNonce(negotiation, nonce);
    }

    /// <summary>
    /// Our <c>commitment_signed</c> for the open's funding transaction <paramref name="fundingTxId"/> again (RFC 6979:
    /// the same signature as the first time), when it is the negotiation's pending transaction; empty otherwise. A
    /// simple taproot open without the peer's <c>current_commit_nonce</c> answers our <c>tx_abort</c> instead while our
    /// <c>tx_signatures</c> are not sent (NL-969).
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> CreateCommitmentSignedRetransmissionAsync(ChannelModel channel,
                                                                                              TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsPendingOpen(channel))
            return [];

        var negotiation = await _service.GetOrLoadAsync(channel.ChannelId, _unitOfWork, CancellationToken.None);
        if (negotiation?.PendingTxId is not { } pending || !pending.Equals(fundingTxId))
        {
            _logger.LogInformation("No pending dual-funded transaction {TxId} on channel {ChannelId} to sign again",
                                   fundingTxId, channel.ChannelId);
            return [];
        }

        if (_service.CreateCommitmentSignedRetransmission(negotiation) is { } commitmentSigned)
            return [commitmentSigned];

        return channel.ChannelParams.OptionSimpleTaproot
                   ? await _service.AbortForMissingCommitNonceAsync(negotiation, _unitOfWork)
                   : [];
    }
}