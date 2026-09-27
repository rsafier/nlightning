namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.Payloads;

/// <summary>
/// <see cref="IOnionMessageUnwrapper"/>: peel via <see cref="ISphinxService"/>, then unblind via
/// <see cref="IRouteBlindingService"/> with the path_key shared secret the peel already computed (one node-key ECDH
/// for both).
/// </summary>
internal sealed class OnionMessageUnwrapper : IOnionMessageUnwrapper
{
    private readonly ISphinxService _sphinxService;
    private readonly IRouteBlindingService _routeBlindingService;

    public OnionMessageUnwrapper(ISphinxService sphinxService, IRouteBlindingService routeBlindingService)
    {
        _sphinxService = sphinxService;
        _routeBlindingService = routeBlindingService;
    }

    /// <inheritdoc />
    public OnionMessageUnwrapResult UnwrapAsLocalNode(OnionMessageMessage message) =>
        Unwrap(message, (packet, pathKey) =>
                   _sphinxService.PeelAsLocalNode(packet, ReadOnlySpan<byte>.Empty, pathKey,
                                                  OnionPacketKind.OnionMessage));

    /// <inheritdoc />
    public OnionMessageUnwrapResult Unwrap(OnionMessageMessage message, PrivKey nodeKey) =>
        Unwrap(message, (packet, pathKey) =>
                   _sphinxService.Peel(packet, ReadOnlySpan<byte>.Empty, nodeKey, pathKey,
                                       OnionPacketKind.OnionMessage));

    private OnionMessageUnwrapResult Unwrap(OnionMessageMessage message,
                                            Func<OnionPacket, CompactPubKey, PeeledOnion> peel)
    {
        ArgumentNullException.ThrowIfNull(message);

        var pathKey = message.Payload.PathKey;
        PeeledOnion peeled;
        BlindedHopUnblinding unblinding;
        OnionMessageTlvs? tlvs;
        try
        {
            var raw = message.Payload.OnionMessagePacket.Span;
            var packet = new OnionPacket(raw, raw.Length - OnionConstants.PacketOverheadLength);
            peeled = peel(packet, pathKey);

            if (!OnionMessagePayloadCodec.TryDecode(peeled.Payload.Span, out tlvs, out var reason))
                return OnionMessageUnwrapResult.Ignore(reason!);

            if (tlvs!.EncryptedRecipientData is not { } encryptedRecipientData)
                return OnionMessageUnwrapResult.Ignore("no encrypted_recipient_data");

            unblinding = _routeBlindingService.UnblindAsLocalNode(pathKey, encryptedRecipientData,
                                                                  peeled.PathKeySharedSecret);
        }
        catch (OnionException e)
        {
            return OnionMessageUnwrapResult.Ignore($"{e.FailureCode}: {e.Message}");
        }
        catch (ArgumentException e)
        {
            return OnionMessageUnwrapResult.Ignore(e.Message);
        }

        var data = unblinding.RecipientData;

        // BOLT 4 reader: allowed_features with an unknown bit (every bit: none is defined) → ignore
        if (data.HasAnyAllowedFeature)
            return OnionMessageUnwrapResult.Ignore("allowed_features has an unknown bit");

        return peeled.IsFinal ? UnwrapFinal(data, tlvs) : UnwrapNonFinal(data, tlvs, peeled, unblinding);
    }

    private static OnionMessageUnwrapResult UnwrapNonFinal(BlindedRecipientData data, OnionMessageTlvs tlvs,
                                                           PeeledOnion peeled, BlindedHopUnblinding unblinding)
    {
        if (tlvs.ReplyPath is not null || tlvs.OtherRecords.Count > 0)
            return OnionMessageUnwrapResult.Ignore("a non-final onionmsg_tlv carries more than encrypted_recipient_data");
        if (data.PathId is not null)
            return OnionMessageUnwrapResult.Ignore("a non-final encrypted_data_tlv carries a path_id");
        if (data.NextNodeId is null && data.ShortChannelId is null)
            return OnionMessageUnwrapResult.Ignore("a non-final encrypted_data_tlv names no next hop");

        var next = new OnionMessageMessage(new OnionMessagePayload(unblinding.NextPathKey,
                                                                   peeled.NextPacket!.Value.ToBytes()));
        return OnionMessageUnwrapResult.Forward(data, next);
    }

    private static OnionMessageUnwrapResult UnwrapFinal(BlindedRecipientData data, OnionMessageTlvs tlvs)
    {
        var payloadFields = tlvs.OtherRecords.Count(r => r.Type >= OnionMessageConstants.FirstPayloadFieldType);
        return payloadFields > 1
                   ? OnionMessageUnwrapResult.Ignore("the final onionmsg_tlv has more than one payload field")
                   : OnionMessageUnwrapResult.Deliver(data, tlvs);
    }
}