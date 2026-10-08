using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Payments.Keysend;
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

        // A node crash can leave an incoming signing workflow with its original updates already on disk.
        // The peer retransmits the whole diff. Only those exact, still-uncommitted inputs may be ignored.
        if (await _transitions.HasPendingVlsHolderValidationAsync(channel)
         && channel.Commitments!.GetHtlc(HtlcDirection.Incoming, payload.Id) is { } saved)
        {
            if (saved.State != HtlcState.RcvdAddHtlc || saved.Removal is not null
             || saved.AmountMsat != payload.Amount.MilliSatoshi
             || !((byte[])saved.PaymentHash).AsSpan().SequenceEqual(payload.PaymentHash.Span)
             || saved.CltvExpiry != payload.CltvExpiry
             || !saved.OnionRoutingPacket.Span.SequenceEqual(payload.OnionRoutingPacket.Span)
             || saved.PathKey != message.BlindedPathTlv?.PathKey
             || !saved.WireCustomRecords.Span.SequenceEqual(WireCustomRecordCodec.Encode(message.CustomRecords))
             || !HasCanonicalExtension(message))
                throw new ChannelWarningException(
                    "Replayed update_add_htlc does not match the durable VLS holder-validation input",
                    payload.ChannelId, "Replayed update_add_htlc differs from the pending signing input")
                {
                    CloseConnection = true
                };
            return [];
        }

        CommitmentsResult result;
        try
        {
            result = channel.Commitments!.ReceiveAdd(payload.Id, payload.Amount.MilliSatoshi,
                                                     new Hash(payload.PaymentHash.ToArray()), payload.CltvExpiry,
                                                     payload.OnionRoutingPacket, message.BlindedPathTlv?.PathKey,
                                                     WireCustomRecordCodec.Encode(message.CustomRecords));
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        _transitions.ValidateVlsIncomingDust(channel, result.Next);
        await _transitions.CommitAsync(channel, result);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Received HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}", payload.Id,
                             payload.Amount.MilliSatoshi, payload.ChannelId);

        return [];
    }
    private static bool HasCanonicalExtension(UpdateAddHtlcMessage message)
    {
        var tlvs = message.Extension?.GetTlvs().ToArray() ?? [];
        var withPath = message.BlindedPathTlv is not null;
        if (tlvs.Length != message.CustomRecords.Count + (withPath ? 1 : 0)) return false;
        var index = 0;
        if (message.BlindedPathTlv is { } path)
        {
            if (tlvs[index].Type.Value != 0
             || !tlvs[index].Value.AsSpan().SequenceEqual((byte[])path.PathKey)) return false;
            index++;
        }
        foreach (var record in message.CustomRecords)
        {
            if (tlvs[index].Type.Value != record.Type
             || !tlvs[index].Value.AsSpan().SequenceEqual(record.Value.Span)) return false;
            index++;
        }
        return true;
    }

}