using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Infrastructure.Crypto.Hashes;
using Interfaces;
using Services;

/// <summary>
/// Receives <c>update_fulfill_htlc</c> for an HTLC we offered (BOLT 2, plan N6-T1): the engine checks the id and the
/// preimage (B2-DEL-R01, R02, R07), and the preimage is persisted before anything uses it (I10); the
/// <c>OutgoingHtlcFulfilled</c> event goes to the HTLC switch at once (B2-FWD-05). The <c>attribution_data</c> and
/// <c>fulfillment_payload</c> TLVs are persisted with the fulfill and carried by the event (BOLT 4, NL-326); a
/// <c>fulfillment_payload</c> longer than 32768 bytes fails the channel (BOLT 2 MUST, NL-325), after a valid preimage
/// of it was persisted and handed to the switch without the payload (fund safety).
/// </summary>
/// <remarks>The preimage is hashed with its own <see cref="Sha256"/>: the registered <c>ISha256</c> is a stateful
/// singleton, and peers' messages are handled concurrently.</remarks>
public class UpdateFulfillHtlcMessageHandler : IChannelMessageHandler<UpdateFulfillHtlcMessage>
{
    private readonly ILogger<UpdateFulfillHtlcMessageHandler> _logger;
    private readonly ChannelStateTransitionService _transitions;

    public UpdateFulfillHtlcMessageHandler(ILogger<UpdateFulfillHtlcMessageHandler> logger,
                                           ChannelStateTransitionService transitions)
    {
        _logger = logger;
        _transitions = transitions;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(UpdateFulfillHtlcMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channel = _transitions.GetUpdatableChannel(payload.ChannelId, currentState, "update_fulfill_htlc");

        // BOLT 2: "if the fulfillment_payload in update_fulfill_htlc is longer than 32768 bytes: MUST send an error
        // and fail the channel" (NL-325). A valid preimage is kept first: when we forwarded this HTLC the downstream
        // peer can still claim it on chain, so the preimage must be persisted and reach the switch (upstream fulfill)
        // before the channel fails, or we lose the forwarded amount.
        if (message.FulfillmentPayloadTlv is { IsTooLong: true } tooLong)
        {
            await KeepPreimageBeforeFailingAsync(channel, message);
            throw new ChannelFailedException(payload.ChannelId,
                                             $"update_fulfill_htlc {payload.Id} carries a fulfillment_payload of "
                                           + $"{tooLong.FulfillmentPayload.Length} bytes (more than "
                                           + $"{OnionConstants.MaxFulfillmentPayloadLength})",
                                             peerMessage: "fulfillment_payload longer than 32768 bytes");
        }

        CommitmentsResult result;
        try
        {
            using var sha256 = new Sha256();
            result = channel.Commitments!.ReceiveFulfill(payload.Id, new Secret(payload.PaymentPreimage.ToArray()),
                                                         sha256, message.AttributionDataTlv?.AttributionData ?? [],
                                                         message.FulfillmentPayloadTlv?.FulfillmentPayload ?? []);
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        await _transitions.CommitAsync(channel, result);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HTLC {HtlcId} on channel {ChannelId} was fulfilled by the peer", payload.Id,
                             payload.ChannelId);

        return [];
    }

    /// <summary>
    /// Applies a fulfill whose <c>fulfillment_payload</c> is too long without that payload, when its id and preimage
    /// are valid: the preimage is persisted with the removal (<c>KnownPreimage</c>) and <c>OutgoingHtlcFulfilled</c>
    /// is queued (<c>ChannelManager</c> hands queued events to the switch even when the handler then fails the
    /// channel). An invalid id or preimage keeps nothing; the channel fails over the payload either way.
    /// </summary>
    private async Task KeepPreimageBeforeFailingAsync(ChannelModel channel, UpdateFulfillHtlcMessage message)
    {
        var payload = message.Payload;
        CommitmentsResult result;
        try
        {
            using var sha256 = new Sha256();
            result = channel.Commitments!.ReceiveFulfill(payload.Id, new Secret(payload.PaymentPreimage.ToArray()),
                                                         sha256, message.AttributionDataTlv?.AttributionData ?? [],
                                                         ReadOnlyMemory<byte>.Empty);
        }
        catch (CommitmentViolationException e)
        {
            _logger.LogWarning("update_fulfill_htlc {HtlcId} on channel {ChannelId} with an oversized "
                             + "fulfillment_payload is not a valid fulfill either: {Reason}", payload.Id,
                               payload.ChannelId, e.Message);
            return;
        }

        await _transitions.CommitAsync(channel, result);
        _logger.LogWarning("Kept the preimage of HTLC {HtlcId} on channel {ChannelId} before failing the channel over "
                         + "its oversized fulfillment_payload", payload.Id, payload.ChannelId);
    }
}