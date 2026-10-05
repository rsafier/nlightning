using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Crypto.Constants;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The wire definitions of the BOLT 2 HTLC-update messages (<c>update_add_htlc</c> 128 with its mandatory 1366-byte
/// onion and the blinded-path TLV 0, <c>update_fulfill_htlc</c> 130 with the attribution/fulfillment-payload TLVs,
/// <c>update_fail_htlc</c> 131 with attribution_data, <c>update_fail_malformed_htlc</c> 133) and <c>update_fee</c>
/// (134).
/// </summary>
internal static class UpdateAddHtlcWire
{
    public static readonly MessageWire<UpdateAddHtlcMessage> Def = new(MessageTypes.UpdateAddHtlc, Encode, Decode,
        TlvDef.Typed<BlindedPathTlv>(TlvConstants.BlindedPath));

    private static void Encode(ref WireWriter writer, UpdateAddHtlcMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.Id);
        writer.U64((ulong)payload.Amount.MilliSatoshi);
        writer.Bytes(payload.PaymentHash.Span);
        writer.U32(payload.CltvExpiry);
        if (payload.OnionRoutingPacket.Length != OnionConstants.PacketLength)
            throw new SerializationException(
                $"Onion routing packet must be exactly {OnionConstants.PacketLength} bytes");
        writer.Bytes(payload.OnionRoutingPacket.Span);
    }

    private static WireConstruct<UpdateAddHtlcMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var id = reader.U64();
        var amountMsat = reader.U64();
        var paymentHash = reader.BytesArray(CryptoConstants.Sha256HashLen);
        var cltvExpiry = reader.U32();
        var onion = reader.BytesArray(OnionConstants.PacketLength);

        return tlvs => new UpdateAddHtlcMessage(
            new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(amountMsat), channelId, cltvExpiry, id, paymentHash,
                                     onion),
            tlvs.Get<BlindedPathTlv>(TlvConstants.BlindedPath));
    }
}

internal static class UpdateFulfillHtlcWire
{
    public static readonly MessageWire<UpdateFulfillHtlcMessage> Def = new(MessageTypes.UpdateFulfillHtlc, Encode,
        Decode, TlvDef.Typed<AttributionDataTlv>(TlvConstants.AttributionData),
        TlvDef.Typed<FulfillmentPayloadTlv>(TlvConstants.FulfillmentPayload));

    private static void Encode(ref WireWriter writer, UpdateFulfillHtlcMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.Id);
        writer.Bytes(payload.PaymentPreimage.Span);
    }

    private static WireConstruct<UpdateFulfillHtlcMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var id = reader.U64();
        var preimage = reader.BytesArray(CryptoConstants.Sha256HashLen);

        return tlvs => new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(channelId, id, preimage),
                                                    tlvs.Get<AttributionDataTlv>(TlvConstants.AttributionData),
                                                    tlvs.Get<FulfillmentPayloadTlv>(TlvConstants.FulfillmentPayload));
    }
}

internal static class UpdateFailHtlcWire
{
    public static readonly MessageWire<UpdateFailHtlcMessage> Def = new(MessageTypes.UpdateFailHtlc, Encode, Decode,
        TlvDef.Typed<AttributionDataTlv>(TlvConstants.AttributionData));

    private static void Encode(ref WireWriter writer, UpdateFailHtlcMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.Id);
        writer.U16(payload.Len);
        writer.Bytes(payload.Reason.Span);
    }

    private static WireConstruct<UpdateFailHtlcMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var id = reader.U64();
        var length = reader.U16();
        var reason = reader.BytesArray(length);

        return tlvs => new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(channelId, id, reason),
                                                 tlvs.Get<AttributionDataTlv>(TlvConstants.AttributionData));
    }
}

internal static class UpdateFailMalformedHtlcWire
{
    public static readonly MessageWire<UpdateFailMalformedHtlcMessage> Def =
        new(MessageTypes.UpdateFailMalformedHtlc, Encode, Decode);

    private static void Encode(ref WireWriter writer, UpdateFailMalformedHtlcMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.Id);
        writer.Bytes(payload.Sha256OfOnion.Span);
        writer.U16(payload.FailureCode);
    }

    private static WireConstruct<UpdateFailMalformedHtlcMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var id = reader.U64();
        var sha256OfOnion = reader.BytesArray(CryptoConstants.Sha256HashLen);
        var failureCode = reader.U16();

        return tlvs => new UpdateFailMalformedHtlcMessage(
            new UpdateFailMalformedHtlcPayload(channelId, failureCode, id, sha256OfOnion));
    }
}

internal static class UpdateFeeWire
{
    public static readonly MessageWire<UpdateFeeMessage> Def = new(MessageTypes.UpdateFee, Encode, Decode);

    private static void Encode(ref WireWriter writer, UpdateFeeMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.U32(message.Payload.FeeratePerKw);
    }

    private static WireConstruct<UpdateFeeMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var feeratePerKw = reader.U32();

        return tlvs => new UpdateFeeMessage(new UpdateFeePayload(channelId, feeratePerKw));
    }
}