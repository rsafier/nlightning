namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using System.Runtime.Serialization;

using Domain.Crypto.Constants;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The wire definitions of the BOLT 7 gossip query family, <c>query_short_channel_ids</c> (261),
/// <c>reply_short_channel_ids_end</c> (262), <c>query_channel_range</c> (263), <c>reply_channel_range</c> (264) and
/// <c>gossip_timestamp_filter</c> (265): fixed big-endian fields, then the odd advisory TLVs kept as raw records
/// (<c>query_flags</c> 1, <c>query_option</c> 1, <c>reply_channel_range</c> timestamps 1 / checksums 3 — the known set
/// rejects unknown even types, BOLT 1). The query TLVs are advisory, so the message ctors rebuild them as plain
/// <see cref="BaseTlv"/>s and a body failure surfaces as <see cref="PayloadSerializationException"/> while an
/// extension failure is a <see cref="MessageSerializationException"/>, like the hand-written pair.
/// </summary>
internal static class QueryShortChannelIdsWire
{
    public static readonly MessageWire<QueryShortChannelIdsMessage> Def =
        new(MessageTypes.QueryShortChannelIds, Encode, Decode, TlvDef.RawKnown(TlvConstants.QueryFlags));

    private static void Encode(ref WireWriter writer, QueryShortChannelIdsMessage message)
    {
        writer.Bytes(message.Payload.ChainHash);
        writer.WriteU16Prefixed(message.Payload.EncodedShortIds);
    }

    private static WireConstruct<QueryShortChannelIdsMessage> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var encodedShortIds = reader.BytesArray(reader.U16());

        return tlvs => new QueryShortChannelIdsMessage(
            new QueryShortChannelIdsPayload(chainHash, encodedShortIds),
            tlvs.RawTlv(TlvConstants.QueryFlags));
    }
}

internal static class ReplyShortChannelIdsEndWire
{
    public static readonly MessageWire<ReplyShortChannelIdsEndMessage> Def =
        new(MessageTypes.ReplyShortChannelIdsEnd, Encode, Decode);

    private static void Encode(ref WireWriter writer, ReplyShortChannelIdsEndMessage message)
    {
        writer.Bytes(message.Payload.ChainHash);
        writer.U8(message.Payload.FullInformation ? (byte)1 : (byte)0);
    }

    private static WireConstruct<ReplyShortChannelIdsEndMessage> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var fullInformation = reader.U8() != 0;

        return _ => new ReplyShortChannelIdsEndMessage(
            new ReplyShortChannelIdsEndPayload(chainHash, fullInformation));
    }
}

internal static class QueryChannelRangeWire
{
    public static readonly MessageWire<QueryChannelRangeMessage> Def =
        new(MessageTypes.QueryChannelRange, Encode, Decode, TlvDef.RawKnown(TlvConstants.QueryOption));

    private static void Encode(ref WireWriter writer, QueryChannelRangeMessage message)
    {
        writer.Bytes(message.Payload.ChainHash);
        writer.U32(message.Payload.FirstBlocknum);
        writer.U32(message.Payload.NumberOfBlocks);
    }

    private static WireConstruct<QueryChannelRangeMessage> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var firstBlocknum = reader.U32();
        var numberOfBlocks = reader.U32();

        return tlvs => new QueryChannelRangeMessage(
            new QueryChannelRangePayload(chainHash, firstBlocknum, numberOfBlocks),
            tlvs.RawTlv(TlvConstants.QueryOption));
    }
}

internal static class ReplyChannelRangeWire
{
    public static readonly MessageWire<ReplyChannelRangeMessage> Def =
        new(MessageTypes.ReplyChannelRange, Encode, Decode,
            TlvDef.RawKnown(TlvConstants.ReplyChannelRangeTimestamps),
            TlvDef.RawKnown(TlvConstants.ReplyChannelRangeChecksums));

    private static void Encode(ref WireWriter writer, ReplyChannelRangeMessage message)
    {
        writer.Bytes(message.Payload.ChainHash);
        writer.U32(message.Payload.FirstBlocknum);
        writer.U32(message.Payload.NumberOfBlocks);
        writer.U8(message.Payload.SyncComplete ? (byte)1 : (byte)0);
        writer.WriteU16Prefixed(message.Payload.EncodedShortIds);
    }

    private static WireConstruct<ReplyChannelRangeMessage> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var firstBlocknum = reader.U32();
        var numberOfBlocks = reader.U32();
        var syncComplete = reader.U8() != 0;
        var encodedShortIds = reader.BytesArray(reader.U16());

        return tlvs => new ReplyChannelRangeMessage(
            new ReplyChannelRangePayload(chainHash, firstBlocknum, numberOfBlocks, syncComplete, encodedShortIds),
            tlvs.RawTlv(TlvConstants.ReplyChannelRangeTimestamps),
            tlvs.RawTlv(TlvConstants.ReplyChannelRangeChecksums));
    }
}

internal static class GossipTimestampFilterWire
{
    public static readonly MessageWire<GossipTimestampFilterMessage> Def =
        new(MessageTypes.GossipTimestampFilter, Encode, Decode);

    private static void Encode(ref WireWriter writer, GossipTimestampFilterMessage message)
    {
        writer.Bytes(message.Payload.ChainHash);
        writer.U32(message.Payload.FirstTimestamp);
        writer.U32(message.Payload.TimestampRange);
    }

    private static WireConstruct<GossipTimestampFilterMessage> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var firstTimestamp = reader.U32();
        var timestampRange = reader.U32();

        return _ => new GossipTimestampFilterMessage(
            new GossipTimestampFilterPayload(chainHash, firstTimestamp, timestampRange));
    }
}

/// <summary>Extensions the gossip query definitions share.</summary>
internal static class GossipQueryWireExtensions
{
    /// <summary>A u16-length-prefixed field, bounded like the hand-written payload serializer was.</summary>
    public static void WriteU16Prefixed(this ref WireWriter writer, ReadOnlyMemory<byte> data)
    {
        if (data.Length > ushort.MaxValue)
            throw new SerializationException($"Field is too long ({data.Length} bytes) for a u16 length prefix");

        writer.U16((ushort)data.Length);
        writer.Bytes(data.Span);
    }

    /// <summary>The raw record of an advisory gossip-query TLV rebuilt for the message ctor, or null when absent.</summary>
    public static BaseTlv? RawTlv(this WireTlvs tlvs, BigSize type)
    {
        return tlvs.Get<byte[]>(type) is { } value ? new BaseTlv(type, value) : null;
    }
}