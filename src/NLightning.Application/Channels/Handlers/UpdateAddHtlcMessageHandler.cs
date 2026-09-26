using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;
using Services;

/// <summary>
/// Receives <c>update_add_htlc</c> (BOLT 2, plan N6-T1): the commitment engine checks the receiver rules (B2-ADD-R*)
/// and the add is persisted. Nothing is sent: the peer's <c>commitment_signed</c> commits it, and the onion is only
/// peeled once the HTLC is locked in (<c>IncomingHtlcLockedIn</c>, B2-FWD-01).
/// </summary>
public class UpdateAddHtlcMessageHandler : IChannelMessageHandler<UpdateAddHtlcMessage>
{
    private readonly ILogger<UpdateAddHtlcMessageHandler> _logger;
    private readonly ChannelStateTransitionService _transitions;

    public UpdateAddHtlcMessageHandler(ILogger<UpdateAddHtlcMessageHandler> logger,
                                       ChannelStateTransitionService transitions)
    {
        _logger = logger;
        _transitions = transitions;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(UpdateAddHtlcMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channel = _transitions.GetUpdatableChannel(payload.ChannelId, currentState, "update_add_htlc");

        // B2-SHUT-R06: BOLT 2 forbids an update_add_htlc after the sender's own shutdown (one that crosses ours is fine)
        if (channel is { State: ChannelState.ShuttingDown, RemoteShutdownScript: not null })
            throw new ChannelWarningException(
                $"[B2-SHUT-R06] update_add_htlc {payload.Id} on channel {payload.ChannelId} after the peer's shutdown",
                payload.ChannelId, "update_add_htlc after shutdown")
            {
                CloseConnection = true
            };

        CommitmentsResult result;
        try
        {
            result = channel.Commitments!.ReceiveAdd(payload.Id, payload.Amount.MilliSatoshi,
                                                     new Hash(payload.PaymentHash.ToArray()), payload.CltvExpiry,
                                                     payload.OnionRoutingPacket, message.BlindedPathTlv?.PathKey);
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        await _transitions.CommitAsync(channel, result);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Received HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}", payload.Id,
                             payload.Amount.MilliSatoshi, payload.ChannelId);

        return [];
    }
}