namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of <c>error</c> (17) and <c>warning</c> (1), which share <see cref="ErrorPayload"/>:
/// <c>channel_id</c> then <c>u16 len || data</c> (a null data encodes as a zero length, as the hand-written
/// serializer did).
/// </summary>
internal static class ErrorWire
{
    public static readonly MessageWire<ErrorMessage> Def = new(MessageTypes.Error, EncodeError, DecodeError);

    internal static void Encode(ref WireWriter writer, ChannelId channelId, byte[]? data)
    {
        writer.ChannelId(channelId);
        if (data is null)
        {
            writer.U16(0);
            return;
        }

        writer.U16((ushort)data.Length);
        writer.Bytes(data);
    }

    internal static (ChannelId ChannelId, byte[] Data) Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var length = reader.U16();
        return (channelId, reader.BytesArray(length));
    }

    private static void EncodeError(ref WireWriter writer, ErrorMessage message) =>
        Encode(ref writer, message.Payload.ChannelId, message.Payload.Data);

    private static WireConstruct<ErrorMessage> DecodeError(ref WireReader reader)
    {
        var (channelId, data) = Decode(ref reader);
        return tlvs => new ErrorMessage(new ErrorPayload(channelId, data));
    }
}

internal static class WarningWire
{
    public static readonly MessageWire<WarningMessage> Def = new(MessageTypes.Warning, EncodeWarning, DecodeWarning);

    private static void EncodeWarning(ref WireWriter writer, WarningMessage message) =>
        ErrorWire.Encode(ref writer, message.Payload.ChannelId, message.Payload.Data);

    private static WireConstruct<WarningMessage> DecodeWarning(ref WireReader reader)
    {
        var (channelId, data) = ErrorWire.Decode(ref reader);
        return tlvs => new WarningMessage(new ErrorPayload(channelId, data));
    }
}