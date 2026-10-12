namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using System.Runtime.Serialization;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definition of BOLT 4 <c>onion_message</c> (513): <c>point path_key</c> ‖ <c>u16 len</c> ‖
/// <c>len*byte onion_message_packet</c>. It defines no TLV extension, so trailing bytes are validated with an empty
/// known set (BOLT 1: unknown even fails, odd ignored).
/// </summary>
internal static class OnionMessageWire
{
    public static readonly MessageWire<OnionMessageMessage> Def =
        new(MessageTypes.OnionMessage, Encode, Decode, strictEmptyExtension: true, keepRawExtension: false);

    private static void Encode(ref WireWriter writer, OnionMessageMessage message)
    {
        var payload = message.Payload;
        writer.CompactPubKey(payload.PathKey);
        writer.U16((ushort)payload.OnionMessagePacket.Length);
        writer.Bytes(payload.OnionMessagePacket.Span);
    }

    private static WireConstruct<OnionMessageMessage> Decode(ref WireReader reader)
    {
        var pathKey = reader.CompactPubKey();
        var length = reader.U16();
        if (length < OnionConstants.PacketOverheadLength)
            throw new SerializationException(
                $"onion_message len {length} is below the {OnionConstants.PacketOverheadLength}-byte packet overhead");
        var packet = reader.BytesArray(length);

        return tlvs => new OnionMessageMessage(new OnionMessagePayload(pathKey, packet));
    }
}