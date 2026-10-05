namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of the BOLT 1 ping/pong pair and the shared <c>WireWriter</c>/<c>WireReader</c> extensions
/// for the BOLT 2 fixed-field value objects.
/// </summary>
internal static class PingWire
{
    public static readonly MessageWire<PingMessage> Def = new(MessageTypes.Ping, Encode, Decode);

    private static void Encode(ref WireWriter writer, PingMessage message)
    {
        writer.U16(message.Payload.NumPongBytes);
        writer.U16(message.Payload.BytesLength);
        writer.Bytes(message.Payload.Ignored);
    }

    private static WireConstruct<PingMessage> Decode(ref WireReader reader)
    {
        var numPongBytes = reader.U16();
        var bytesLength = reader.U16();
        var ignored = reader.BytesArray(bytesLength);

        return tlvs => new PingMessage(
            new PingPayload { NumPongBytes = numPongBytes, BytesLength = bytesLength, Ignored = ignored });
    }
}

internal static class PongWire
{
    public static readonly MessageWire<PongMessage> Def = new(MessageTypes.Pong, Encode, Decode);

    private static void Encode(ref WireWriter writer, PongMessage message)
    {
        writer.U16(message.Payload.BytesLength);
        writer.Bytes(message.Payload.Ignored);
    }

    private static WireConstruct<PongMessage> Decode(ref WireReader reader)
    {
        var bytesLength = reader.U16();
        var ignored = reader.BytesArray(bytesLength);

        return tlvs => new PongMessage(new PongPayload(bytesLength) { Ignored = ignored });
    }
}

/// <summary>Extensions the definitions use for the BOLT 2 fixed-field value objects.</summary>
internal static class WireExtensions
{
    public static void ChannelId(this ref WireWriter writer, ChannelId channelId) => writer.Bytes(channelId);

    public static ChannelId ChannelId(this ref WireReader reader) => new(reader.Bytes(32));

    public static void CompactPubKey(this ref WireWriter writer, Domain.Crypto.ValueObjects.CompactPubKey point) =>
        writer.Bytes(point);

    public static Domain.Crypto.ValueObjects.CompactPubKey CompactPubKey(this ref WireReader reader) =>
        new(reader.BytesArray(33));
}