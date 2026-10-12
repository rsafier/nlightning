namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of BOLT 7 <c>channel_announcement</c> (256), <c>node_announcement</c> (257) and
/// <c>channel_update</c> (258): the Domain payload is the single codec (plan decision D1), so the definition only
/// frames it — the decode reads the whole remaining message and hands it to <c>Payload.Parse</c>, keeping unknown
/// trailing fields (which the signatures cover) in <c>Payload.ExtraData</c> byte-verbatim. Neither message has a TLV
/// extension; a malformed payload surfaces as <see cref="PayloadSerializationException"/> like the hand-written pair.
/// </summary>
internal static class ChannelAnnouncementWire
{
    public static readonly MessageWire<ChannelAnnouncementMessage> Def =
        new(MessageTypes.ChannelAnnouncement, Encode, Decode);

    private static void Encode(ref WireWriter writer, ChannelAnnouncementMessage message)
    {
        writer.Bytes(message.Payload.GetBytes());
    }

    private static WireConstruct<ChannelAnnouncementMessage> Decode(ref WireReader reader)
    {
        var bytes = reader.RemainingBytes().ToArray();
        return _ => new ChannelAnnouncementMessage(ChannelAnnouncementPayload.Parse(bytes));
    }
}

internal static class NodeAnnouncementWire
{
    public static readonly MessageWire<NodeAnnouncementMessage> Def =
        new(MessageTypes.NodeAnnouncement, Encode, Decode);

    private static void Encode(ref WireWriter writer, NodeAnnouncementMessage message)
    {
        writer.Bytes(message.Payload.GetBytes());
    }

    private static WireConstruct<NodeAnnouncementMessage> Decode(ref WireReader reader)
    {
        var bytes = reader.RemainingBytes().ToArray();
        return _ => new NodeAnnouncementMessage(NodeAnnouncementPayload.Parse(bytes));
    }
}

internal static class ChannelUpdateWire
{
    public static readonly MessageWire<ChannelUpdateMessage> Def =
        new(MessageTypes.ChannelUpdate, Encode, Decode);

    private static void Encode(ref WireWriter writer, ChannelUpdateMessage message)
    {
        writer.Bytes(message.Payload.GetBytes());
    }

    private static WireConstruct<ChannelUpdateMessage> Decode(ref WireReader reader)
    {
        var bytes = reader.RemainingBytes().ToArray();
        return _ => new ChannelUpdateMessage(ChannelUpdatePayload.Parse(bytes));
    }
}

/// <summary>
/// The wire definition of BOLT 7 <c>announcement_signatures</c> (259): the four fixed fields, then the TLV extension
/// (none is defined) read strictly — BOLT 1: an unknown even type fails the message, an unknown odd type is ignored
/// but kept byte-verbatim in <see cref="AnnouncementSignaturesPayload.ExtraData"/> so parse → serialize is
/// byte-identical. Body failures surface as <see cref="PayloadSerializationException"/>, extension failures as
/// <see cref="MessageSerializationException"/>, like the hand-written pair.
/// </summary>
internal static class AnnouncementSignaturesWire
{
    public static readonly MessageWire<AnnouncementSignaturesMessage> Def =
        new(MessageTypes.AnnouncementSignatures, Encode, Decode, strictEmptyExtension: true, keepRawExtension: true);

    private static void Encode(ref WireWriter writer, AnnouncementSignaturesMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.Bytes(payload.ShortChannelId);
        writer.Bytes(payload.NodeSignature);
        writer.Bytes(payload.BitcoinSignature);
        // the TLV extension (none is defined) is not the message's Extension, it lives in the payload verbatim
        writer.Bytes(payload.ExtraData.Span);
    }

    private static WireConstruct<AnnouncementSignaturesMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var shortChannelId = new ShortChannelId(reader.BytesArray(ShortChannelId.Length));
        var nodeSignature = new CompactSignature(reader.BytesArray(AnnouncementSignaturesPayload.SignatureLength));
        var bitcoinSignature = new CompactSignature(reader.BytesArray(AnnouncementSignaturesPayload.SignatureLength));

        return tlvs => new AnnouncementSignaturesMessage(new AnnouncementSignaturesPayload(
            channelId, shortChannelId, nodeSignature, bitcoinSignature,
            tlvs.RawBytes ?? ReadOnlyMemory<byte>.Empty));
    }
}