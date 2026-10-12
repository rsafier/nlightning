namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definition of BOLT 2 <c>stfu</c> (2, "Channel Quiescence"): <c>channel_id</c> ‖ <c>u8 initiator</c>,
/// with no TLV extension (trailing bytes were ignored by the hand-written pair too).
/// </summary>
internal static class StfuWire
{
    public static readonly MessageWire<StfuMessage> Def = new(MessageTypes.Stfu, Encode, Decode);

    private static void Encode(ref WireWriter writer, StfuMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.U8((byte)(message.Payload.Initiator ? 1 : 0));
    }

    private static WireConstruct<StfuMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var initiator = reader.U8() == 1;

        return tlvs => new StfuMessage(new StfuPayload(channelId, initiator));
    }
}