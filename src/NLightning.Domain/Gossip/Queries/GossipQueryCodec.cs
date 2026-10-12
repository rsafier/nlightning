using System.Buffers.Binary;

namespace NLightning.Domain.Gossip.Queries;

using Channels.ValueObjects;
using Exceptions;
using Protocol.Tlv;

/// <summary>
/// The strict codec of the BOLT 7 gossip query fields (plan BOLT7 G3-T1/G3-T4): <c>encoded_short_ids</c>,
/// <c>encoded_query_flags</c>, <c>query_option_flags</c>, <c>encoded_timestamps</c> and the <c>checksums_tlv</c>.
/// </summary>
/// <remarks>
/// Only encoding type 0 (an uncompressed array) is read or written: BOLT 7 says encoding 1 (zlib) "MUST NOT be used"
/// (plan D5), so it is answered with a warning like any unknown encoding. Every decode error is a
/// <see cref="WarningException"/> naming the field, because BOLT 7 lets the receiver of a malformed query or reply send
/// a <c>warning</c>.
/// </remarks>
public static class GossipQueryCodec
{
    /// <summary>Encoding type 0: an uncompressed array, in ascending order for short channel ids.</summary>
    public const byte EncodingUncompressed = 0;

    /// <summary>Encoding type 1: the retired zlib encoding (BOLT 7: MUST NOT be used).</summary>
    public const byte EncodingZlib = 1;

    /// <summary><c>query_flags</c> bit 0: the sender wants the <c>channel_announcement</c>.</summary>
    public const ulong QueryFlagChannelAnnouncement = 1 << 0;

    /// <summary><c>query_flags</c> bit 1: the sender wants the <c>channel_update</c> of <c>node_id_1</c>.</summary>
    public const ulong QueryFlagChannelUpdate1 = 1 << 1;

    /// <summary><c>query_flags</c> bit 2: the sender wants the <c>channel_update</c> of <c>node_id_2</c>.</summary>
    public const ulong QueryFlagChannelUpdate2 = 1 << 2;

    /// <summary><c>query_flags</c> bit 3: the sender wants the <c>node_announcement</c> of <c>node_id_1</c>.</summary>
    public const ulong QueryFlagNodeAnnouncement1 = 1 << 3;

    /// <summary><c>query_flags</c> bit 4: the sender wants the <c>node_announcement</c> of <c>node_id_2</c>.</summary>
    public const ulong QueryFlagNodeAnnouncement2 = 1 << 4;

    /// <summary>Every <c>query_flags</c> bit BOLT 7 defines.</summary>
    public const ulong QueryFlagAll = QueryFlagChannelAnnouncement | QueryFlagChannelUpdate1 | QueryFlagChannelUpdate2
                                    | QueryFlagNodeAnnouncement1 | QueryFlagNodeAnnouncement2;

    /// <summary><c>query_option_flags</c> bit 0: the sender wants timestamps.</summary>
    public const ulong QueryOptionTimestamps = 1 << 0;

    /// <summary><c>query_option_flags</c> bit 1: the sender wants checksums.</summary>
    public const ulong QueryOptionChecksums = 1 << 1;

    /// <summary>The bytes of one <c>channel_update_timestamps</c> or <c>channel_update_checksums</c> entry.</summary>
    public const int PerChannelPairLength = 2 * sizeof(uint);

    /// <summary>
    /// Decodes <c>encoded_short_ids</c> (encoding type byte first).
    /// </summary>
    /// <param name="encoded">The field.</param>
    /// <param name="messageName">The message, for the warning text.</param>
    /// <exception cref="WarningException">The encoding type is missing or not 0 (zlib or unknown), or the data is not
    /// a whole number of short channel ids.</exception>
    public static ShortChannelId[] DecodeShortChannelIds(ReadOnlySpan<byte> encoded, string messageName)
    {
        var data = ReadEncoding(encoded, messageName, "encoded_short_ids");
        if (data.Length % ShortChannelId.Length != 0)
            throw new WarningException(
                $"{messageName}: encoded_short_ids is not a whole number of short_channel_ids");

        var ids = new ShortChannelId[data.Length / ShortChannelId.Length];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = new ShortChannelId(BinaryPrimitives.ReadUInt64BigEndian(data[(i * ShortChannelId.Length)..]));

        return ids;
    }

    /// <summary>
    /// Encodes short channel ids with encoding type 0 (the caller orders them; BOLT 7 wants ascending order).
    /// </summary>
    public static byte[] EncodeShortChannelIds(IReadOnlyList<ShortChannelId> shortChannelIds)
    {
        ArgumentNullException.ThrowIfNull(shortChannelIds);
        var bytes = new byte[1 + shortChannelIds.Count * ShortChannelId.Length];
        bytes[0] = EncodingUncompressed;
        for (var i = 0; i < shortChannelIds.Count; i++)
            ((ReadOnlySpan<byte>)shortChannelIds[i]).CopyTo(bytes.AsSpan(1 + i * ShortChannelId.Length));

        return bytes;
    }

    /// <summary>
    /// Decodes the <c>query_flags</c> TLV value: the encoding type, then one minimally encoded bigsize per short
    /// channel id.
    /// </summary>
    /// <exception cref="WarningException">The encoding is not 0, a flag is not a minimal bigsize, or there is not
    /// exactly one flag per short channel id.</exception>
    public static ulong[] DecodeQueryFlags(ReadOnlySpan<byte> value, int shortChannelIdCount)
    {
        const string messageName = "query_short_channel_ids";
        var data = ReadEncoding(value, messageName, "query_flags");
        var flags = new List<ulong>(shortChannelIdCount);
        while (!data.IsEmpty)
        {
            if (!BigSizeCodec.TryRead(data, out var flag, out var length))
                throw new WarningException($"{messageName}: query_flags holds a truncated or non-minimal bigsize");

            flags.Add(flag);
            data = data[length..];
        }

        if (flags.Count != shortChannelIdCount)
            throw new WarningException(
                $"{messageName}: query_flags does not decode to one flag per short_channel_id");

        return flags.ToArray();
    }

    /// <summary>
    /// Encodes the <c>query_flags</c> TLV value (encoding type 0, then minimal bigsizes).
    /// </summary>
    public static byte[] EncodeQueryFlags(IReadOnlyList<ulong> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var length = 1 + flags.Sum(BigSizeCodec.GetLength);
        var bytes = new byte[length];
        bytes[0] = EncodingUncompressed;
        var offset = 1;
        foreach (var flag in flags)
            offset += BigSizeCodec.Write(flag, bytes.AsSpan(offset));

        return bytes;
    }

    /// <summary>
    /// Decodes the <c>query_option</c> TLV value (one minimally encoded bigsize).
    /// </summary>
    /// <exception cref="WarningException">The value is not exactly one minimal bigsize.</exception>
    public static ulong DecodeQueryOption(ReadOnlySpan<byte> value)
    {
        if (!BigSizeCodec.TryRead(value, out var flags, out var length) || length != value.Length)
            throw new WarningException("query_channel_range: query_option is not one minimal bigsize");

        return flags;
    }

    /// <summary>
    /// Encodes the <c>query_option</c> TLV value.
    /// </summary>
    public static byte[] EncodeQueryOption(ulong flags)
    {
        var bytes = new byte[BigSizeCodec.GetLength(flags)];
        BigSizeCodec.Write(flags, bytes);
        return bytes;
    }

    /// <summary>
    /// Encodes the <c>timestamps_tlv</c> value: encoding type 0, then one <c>channel_update_timestamps</c> per channel.
    /// </summary>
    public static byte[] EncodeTimestamps(IReadOnlyList<ChannelUpdatePair> timestamps)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        var bytes = new byte[1 + timestamps.Count * PerChannelPairLength];
        bytes[0] = EncodingUncompressed;
        WritePairs(timestamps, bytes.AsSpan(1));
        return bytes;
    }

    /// <summary>
    /// Decodes the <c>timestamps_tlv</c> value.
    /// </summary>
    /// <exception cref="WarningException">The encoding is not 0, or it does not hold exactly
    /// <paramref name="shortChannelIdCount"/> entries.</exception>
    public static ChannelUpdatePair[] DecodeTimestamps(ReadOnlySpan<byte> value, int shortChannelIdCount)
    {
        var data = ReadEncoding(value, "reply_channel_range", "timestamps_tlv");
        return ReadPairs(data, shortChannelIdCount, "timestamps_tlv");
    }

    /// <summary>
    /// Encodes the <c>checksums_tlv</c> value: one <c>channel_update_checksums</c> per channel (no encoding byte).
    /// </summary>
    public static byte[] EncodeChecksums(IReadOnlyList<ChannelUpdatePair> checksums)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        var bytes = new byte[checksums.Count * PerChannelPairLength];
        WritePairs(checksums, bytes);
        return bytes;
    }

    /// <summary>
    /// Decodes the <c>checksums_tlv</c> value.
    /// </summary>
    /// <exception cref="WarningException">It does not hold exactly <paramref name="shortChannelIdCount"/> entries.
    /// </exception>
    public static ChannelUpdatePair[] DecodeChecksums(ReadOnlySpan<byte> value, int shortChannelIdCount) =>
        ReadPairs(value, shortChannelIdCount, "checksums_tlv");

    private static ReadOnlySpan<byte> ReadEncoding(ReadOnlySpan<byte> encoded, string messageName, string field)
    {
        if (encoded.IsEmpty)
            throw new WarningException($"{messageName}: {field} has no encoding type");

        return encoded[0] switch
        {
            EncodingUncompressed => encoded[1..],
            EncodingZlib => throw new WarningException(
                                $"{messageName}: {field} uses the zlib encoding (1), which MUST NOT be used"),
            _ => throw new WarningException($"{messageName}: unknown {field} encoding type {encoded[0]}")
        };
    }

    private static void WritePairs(IReadOnlyList<ChannelUpdatePair> pairs, Span<byte> destination)
    {
        for (var i = 0; i < pairs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[(i * PerChannelPairLength)..], pairs[i].Node1);
            BinaryPrimitives.WriteUInt32BigEndian(destination[(i * PerChannelPairLength + sizeof(uint))..],
                                                  pairs[i].Node2);
        }
    }

    private static ChannelUpdatePair[] ReadPairs(ReadOnlySpan<byte> data, int count, string field)
    {
        if (data.Length != count * PerChannelPairLength)
            throw new WarningException($"reply_channel_range: {field} does not hold one entry per short_channel_id");

        var pairs = new ChannelUpdatePair[count];
        for (var i = 0; i < count; i++)
            pairs[i] = new ChannelUpdatePair(
                BinaryPrimitives.ReadUInt32BigEndian(data[(i * PerChannelPairLength)..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[(i * PerChannelPairLength + sizeof(uint))..]));

        return pairs;
    }
}

/// <summary>
/// One <c>channel_update_timestamps</c> or <c>channel_update_checksums</c> entry: the value for the update of
/// <c>node_id_1</c> and of <c>node_id_2</c> (0 when there is no update from that node).
/// </summary>
public readonly record struct ChannelUpdatePair(uint Node1, uint Node2);