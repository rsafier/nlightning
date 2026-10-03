using System.Security.Cryptography;

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
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;

/// <summary>
/// <see cref="IOnionMessageUnwrapper"/>: peel via <see cref="ISphinxService"/>, then unblind via
/// <see cref="IRouteBlindingService"/> with the path_key shared secret the peel already computed (one node-key ECDH
/// for both). The <c>onionmsg_tlv</c> is read by the Domain <see cref="OnionMessageTlvsCodec"/> (the node's only
/// reader, NL-442), plus a curve check of the <c>reply_path</c> points the Domain codec leaves out.
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

        // Only the parse of the peer's packet bytes is under the ArgumentException catch: an invalid node key is a
        // local fault and must surface from the peel, not drop every message as "ignored".
        OnionPacket packet;
        try
        {
            var raw = message.Payload.OnionMessagePacket.Span;
            packet = new OnionPacket(raw, raw.Length - OnionConstants.PacketOverheadLength);
        }
        catch (ArgumentException e)
        {
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.Undecryptable, e.Message);
        }

        PeeledOnion peeled;
        try
        {
            peeled = peel(packet, pathKey);
        }
        catch (OnionException e)
        {
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.Undecryptable,
                                                   $"{e.FailureCode}: {e.Message}");
        }

        BlindedHopUnblinding unblinding;
        OnionMessageTlvs? tlvs;
        try
        {
            if (!OnionMessageTlvsCodec.TryDecode(peeled.Payload.Span, out tlvs, out var reason))
                return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.InvalidPayload, reason);

            if (tlvs.ReplyPath is { } replyPath && !HasValidPoints(replyPath))
                return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.InvalidPayload,
                                                       "reply_path holds a key that is not a valid point");

            if (tlvs.EncryptedRecipientData is not { } encryptedRecipientData)
                return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.InvalidRecipientData,
                                                       "no encrypted_recipient_data");

            unblinding = _routeBlindingService.UnblindAsLocalNode(pathKey, encryptedRecipientData,
                                                                  peeled.PathKeySharedSecret);
        }
        catch (OnionException e)
        {
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.InvalidRecipientData,
                                                   $"{e.FailureCode}: {e.Message}");
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.InvalidRecipientData, e.Message);
        }
        finally
        {
            // An onion message needs neither shared secret once unblinded (or refused): wipe them now
            ZeroSecrets(peeled);
        }

        var data = unblinding.RecipientData;

        // BOLT 4 reader: allowed_features with an unknown bit (every bit: none is defined) → ignore; a message path's
        // creator MUST NOT include payment_relay or payment_constraints, so data that has them is not ours to follow
        if (data.HasAnyAllowedFeature)
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.ForbiddenRecipientData,
                                                   "allowed_features has an unknown bit");
        if (data.PaymentRelay is not null || data.PaymentConstraints is not null)
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.ForbiddenRecipientData,
                                                   "a message path carries payment_relay or payment_constraints");

        return peeled.IsFinal ? UnwrapFinal(data, tlvs) : UnwrapNonFinal(data, tlvs, peeled, unblinding);
    }

    private static void ZeroSecrets(PeeledOnion peeled)
    {
        CryptographicOperations.ZeroMemory((byte[])peeled.SharedSecret);
        if (peeled.PathKeySharedSecret is { } pathKeySharedSecret)
            CryptographicOperations.ZeroMemory((byte[])pathKeySharedSecret);
    }

    private static OnionMessageUnwrapResult UnwrapNonFinal(BlindedRecipientData data, OnionMessageTlvs tlvs,
                                                           PeeledOnion peeled, BlindedHopUnblinding unblinding)
    {
        if (tlvs.ReplyPath is not null || tlvs.OtherRecords.Count > 0)
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.NonFinalExtraFields,
                                                   "a non-final onionmsg_tlv carries more than "
                                                 + "encrypted_recipient_data");
        if (data.PathId is not null)
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.NonFinalPathId,
                                                   "a non-final encrypted_data_tlv carries a path_id");
        if (data.NextNodeId is null && data.ShortChannelId is null)
            return OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.NoNextHop,
                                                   "a non-final encrypted_data_tlv names no next hop");

        var next = new OnionMessageMessage(new OnionMessagePayload(unblinding.NextPathKey,
                                                                   peeled.NextPacket!.Value.ToBytes()));
        return OnionMessageUnwrapResult.Forward(data, next);
    }

    private static OnionMessageUnwrapResult UnwrapFinal(BlindedRecipientData data, OnionMessageTlvs tlvs)
    {
        return OnionMessageTlvsCodec.CountPayloadFields(tlvs) > 1
                   ? OnionMessageUnwrapResult.Ignore(OnionMessageIgnoreReason.MultiplePayloadFields,
                                                     "the final onionmsg_tlv has more than one payload field")
                   : OnionMessageUnwrapResult.Deliver(data, tlvs);
    }

    /// <summary>
    /// Whether every key of a received <c>reply_path</c> is a point on the curve (the Domain codec checks only the
    /// 02/03 prefix): a path we could never send to is not a valid <c>blinded_path</c>.
    /// </summary>
    private static bool HasValidPoints(WireBlindedPath path)
    {
        if (path.FirstNode.NodeId is { } firstNodeId && !SphinxKeyGenerator.IsValidPublicKey((byte[])firstNodeId))
            return false;
        if (!SphinxKeyGenerator.IsValidPublicKey((byte[])path.FirstPathKey))
            return false;

        return path.Hops.All(hop => SphinxKeyGenerator.IsValidPublicKey((byte[])hop.BlindedNodeId));
    }
}