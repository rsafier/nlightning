using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;
using Services;

/// <summary>
/// Receives <c>commitment_signed</c> (BOLT 2, plan N6-T1): the engine applies the peer's pending updates, verifies the
/// commitment and HTLC signatures (B2-CS-R01..R03) and revokes our previous commitment. The new local commitment is
/// persisted first (B2-CS-R06, D3); only then does the signer release the revoked commitment's secret for the
/// <c>revoke_and_ack</c> (B2-CS-R05, I3). If changes are then pending for the peer (for example the peer's adds, which
/// must reach its commitment too, or our own unsigned updates), our <c>commitment_signed</c> follows the
/// <c>revoke_and_ack</c>.
/// </summary>
/// <remarks>
/// Outside splicing the <c>funding_txid</c> TLV only matters inside a <c>start_batch</c> (BOLT 2), which we never
/// negotiate, so it is not checked here (NL-199 receiver part: nothing to do without splicing).
/// </remarks>
public class CommitmentSignedMessageHandler : IChannelMessageHandler<CommitmentSignedMessage>
{
    private readonly ICommitmentVerifier _commitmentVerifier;
    private readonly ILogger<CommitmentSignedMessageHandler> _logger;
    private readonly ChannelStateTransitionService _transitions;

    public CommitmentSignedMessageHandler(ICommitmentVerifier commitmentVerifier,
                                          ILogger<CommitmentSignedMessageHandler> logger,
                                          ChannelStateTransitionService transitions)
    {
        _commitmentVerifier = commitmentVerifier;
        _logger = logger;
        _transitions = transitions;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(CommitmentSignedMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channel = _transitions.GetUpdatableChannel(payload.ChannelId, currentState, "commitment_signed");

        CommitmentsResult result;
        try
        {
            var signatures = new CommitmentSignatures(payload.Signature, payload.HtlcSignatures.ToList());
            result = channel.Commitments!.ReceiveCommit(signatures, _commitmentVerifier);
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        var revokeAndAck = result.Outbound.OfType<OutboundRevokeAndAck>().Single();

        // Persist the new local commitment with the peer's signatures before the secret exists (B2-CS-R06, I3)
        await _transitions.CommitAsync(channel, result,
                                       new ChannelStateExtras { LastSent = LastSentCommitmentMessage.RevokeAndAck });
        var revokeAndAckMessage = _transitions.CreateRevokeAndAck(channel, revokeAndAck);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Accepted local commitment {Number} of channel {ChannelId}, revoking {Revoked}",
                             channel.Commitments!.LocalCommit.Number, payload.ChannelId,
                             revokeAndAck.RevokedCommitmentNumber);

        var commitmentSigned = await SignFollowUpAsync(channel);
        return commitmentSigned is null ? [revokeAndAckMessage] : [revokeAndAckMessage, commitmentSigned];
    }

    /// <summary>
    /// Signs what is now pending for the peer. A failure here is logged and nothing more is sent: the revoke_and_ack is
    /// already persisted and must still go out; the next trigger signs again.
    /// </summary>
    private async Task<IChannelMessage?> SignFollowUpAsync(ChannelModel channel)
    {
        try
        {
            return await _transitions.SignIfPendingAsync(channel);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to sign the next remote commitment of channel {ChannelId}",
                             channel.ChannelId);
            return null;
        }
    }
}