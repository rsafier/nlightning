namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Protocol.Constants;
using Domain.Protocol.GossipV2;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using A2 = Domain.Protocol.GossipV2.GossipV2Constants.AnnouncementSignatures2;
using CA2 = Domain.Protocol.GossipV2.GossipV2Constants.ChannelAnnouncement2;
using CU2 = Domain.Protocol.GossipV2.GossipV2Constants.ChannelUpdate2;
using NA2 = Domain.Protocol.GossipV2.GossipV2Constants.NodeAnnouncement2;

/// <summary>
/// The wire definitions of taproot gossip (BOLTs PR #1059): <c>announcement_signatures_2</c> (260),
/// <c>channel_announcement_2</c> (267), <c>node_announcement_2</c> (269) and <c>channel_update_2</c> (271). The messages
/// are pure TLV streams with no fixed fields: the TLV table below is the known set (BOLT 1: an unknown even type fails
/// the message; fixed-size records have their exact length checked here), and the extension is kept raw
/// (<c>keepRawExtension</c>) because the signatures cover the records exactly as sent, unknown odd ones included. The
/// Domain payloads parse the kept bytes into typed fields and re-serialize them verbatim
/// (<see cref="PureTlvStream"/>), so a received message is stored, served and relayed byte for byte.
/// </summary>
/// <remarks>
/// A missing required record or a malformed typed field (a non-minimal tu64, a bad point) fails the payload parse,
/// which surfaces as <see cref="PayloadSerializationException"/> (warning and close: the draft's "SHOULD send a
/// warning, MAY close the connection, MUST ignore the message").
/// </remarks>
internal static class AnnouncementSignatures2Wire
{
    public static readonly MessageWire<AnnouncementSignatures2Message> Def = new(
        MessageTypes.AnnouncementSignatures2, Encode, Decode, strictEmptyExtension: false, keepRawExtension: true,
        wrapBodyErrors: false,
        TlvDef.Raw(A2.ChannelId, ChannelConstants.ChannelIdLength),
        TlvDef.Raw(A2.ShortChannelId, ShortChannelId.Length),
        TlvDef.Raw(A2.PartialSignatures, AnnouncementSignatures2Payload.PartialSignaturesLength),
        TlvDef.Raw(A2.FundingTxId, CryptoConstants.Sha256HashLen));

    private static void Encode(ref WireWriter writer, AnnouncementSignatures2Message message) =>
        writer.Bytes(message.Payload.GetBytes());

    private static WireConstruct<AnnouncementSignatures2Message> Decode(ref WireReader reader) =>
        tlvs => new AnnouncementSignatures2Message(AnnouncementSignatures2Payload.Parse(RawStream(tlvs)));

    internal static ReadOnlySpan<byte> RawStream(WireTlvs tlvs) => (tlvs.RawBytes ?? ReadOnlyMemory<byte>.Empty).Span;
}

internal static class ChannelAnnouncement2Wire
{
    public static readonly MessageWire<ChannelAnnouncement2Message> Def = new(
        MessageTypes.ChannelAnnouncement2, Encode, Decode, strictEmptyExtension: false, keepRawExtension: true,
        wrapBodyErrors: false,
        TlvDef.Raw(CA2.ChainHash, CryptoConstants.Sha256HashLen),
        TlvDef.RawKnown(CA2.Features),
        TlvDef.Raw(CA2.ShortChannelId, ShortChannelId.Length),
        TlvDef.RawKnown(CA2.Capacity),
        TlvDef.Raw(CA2.NodeId1, CryptoConstants.CompactPubkeyLen),
        TlvDef.Raw(CA2.NodeId2, CryptoConstants.CompactPubkeyLen),
        TlvDef.Raw(CA2.BitcoinKey1, CryptoConstants.CompactPubkeyLen),
        TlvDef.Raw(CA2.BitcoinKey2, CryptoConstants.CompactPubkeyLen),
        TlvDef.Raw(CA2.MerkleRootHash, CryptoConstants.Sha256HashLen),
        TlvDef.Raw(CA2.Outpoint, ChannelAnnouncement2Payload.OutpointLength),
        TlvDef.Raw(CA2.Signature, GossipV2Constants.SignatureLength));

    private static void Encode(ref WireWriter writer, ChannelAnnouncement2Message message) =>
        writer.Bytes(message.Payload.GetBytes());

    private static WireConstruct<ChannelAnnouncement2Message> Decode(ref WireReader reader) =>
        tlvs => new ChannelAnnouncement2Message(
            ChannelAnnouncement2Payload.Parse(AnnouncementSignatures2Wire.RawStream(tlvs)));
}

internal static class NodeAnnouncement2Wire
{
    public static readonly MessageWire<NodeAnnouncement2Message> Def = new(
        MessageTypes.NodeAnnouncement2, Encode, Decode, strictEmptyExtension: false, keepRawExtension: true,
        wrapBodyErrors: false,
        TlvDef.RawKnown(NA2.Features),
        TlvDef.Raw(NA2.Color, NodeAnnouncement2Payload.ColorLength),
        TlvDef.Raw(NA2.BlockHeight, sizeof(uint)),
        TlvDef.RawKnown(NA2.Alias),
        TlvDef.Raw(NA2.NodeId, CryptoConstants.CompactPubkeyLen),
        TlvDef.RawKnown(NA2.Ipv4Addresses),
        TlvDef.RawKnown(NA2.Ipv6Addresses),
        TlvDef.RawKnown(NA2.TorV3Addresses),
        TlvDef.RawKnown(NA2.DnsHostnames),
        TlvDef.Raw(NA2.Signature, GossipV2Constants.SignatureLength));

    private static void Encode(ref WireWriter writer, NodeAnnouncement2Message message) =>
        writer.Bytes(message.Payload.GetBytes());

    private static WireConstruct<NodeAnnouncement2Message> Decode(ref WireReader reader) =>
        tlvs => new NodeAnnouncement2Message(
            NodeAnnouncement2Payload.Parse(AnnouncementSignatures2Wire.RawStream(tlvs)));
}

internal static class ChannelUpdate2Wire
{
    public static readonly MessageWire<ChannelUpdate2Message> Def = new(
        MessageTypes.ChannelUpdate2, Encode, Decode, strictEmptyExtension: false, keepRawExtension: true,
        wrapBodyErrors: false,
        TlvDef.Raw(CU2.ChainHash, CryptoConstants.Sha256HashLen),
        TlvDef.Raw(CU2.ShortChannelId, ChannelUpdate2Payload.SciddirLength),
        TlvDef.Raw(CU2.BlockHeight, sizeof(uint)),
        TlvDef.Raw(CU2.DisableFlags, 1),
        TlvDef.Raw(CU2.CltvExpiryDelta, sizeof(ushort)),
        TlvDef.RawKnown(CU2.HtlcMinimumMsat),
        TlvDef.RawKnown(CU2.HtlcMaximumMsat),
        TlvDef.RawKnown(CU2.FeeBaseMsat),
        TlvDef.RawKnown(CU2.FeeProportionalMillionths),
        TlvDef.RawKnown(CU2.InboundFeeBaseMsat),
        TlvDef.RawKnown(CU2.InboundFeeProportionalMillionths),
        TlvDef.Raw(CU2.Signature, GossipV2Constants.SignatureLength));

    private static void Encode(ref WireWriter writer, ChannelUpdate2Message message) =>
        writer.Bytes(message.Payload.GetBytes());

    private static WireConstruct<ChannelUpdate2Message> Decode(ref WireReader reader) =>
        tlvs => new ChannelUpdate2Message(ChannelUpdate2Payload.Parse(AnnouncementSignatures2Wire.RawStream(tlvs)));
}