using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.GossipV2;

using Crypto.Constants;
using Crypto.ValueObjects;
using Tlv;

/// <summary>
/// Reads and writes the field types of the v2 gossip records (fixed-length values, <c>u16</c>/<c>u32</c>, truncated
/// integers, points). A malformed value throws <see cref="FormatException"/>, so a payload parse fails the message as
/// a malformed one.
/// </summary>
internal static class PureTlvFields
{
    public static ReadOnlyMemory<byte>? Fixed(PureTlvStream stream, ulong type, int length)
    {
        var value = stream.Get(type);
        if (value is { } v && v.Length != length)
            throw new FormatException($"TLV type {type} holds {v.Length} bytes, not {length}.");

        return value;
    }

    public static ReadOnlyMemory<byte> RequiredFixed(PureTlvStream stream, ulong type, int length) =>
        Fixed(stream, type, length) ?? throw Missing(type);

    public static CompactPubKey? Point(PureTlvStream stream, ulong type)
    {
        if (Fixed(stream, type, CryptoConstants.CompactPubkeyLen) is not { } value)
            return null;

        return ToPoint(value.Span, type);
    }

    public static CompactPubKey RequiredPoint(PureTlvStream stream, ulong type) =>
        Point(stream, type) ?? throw Missing(type);

    public static uint? U32(PureTlvStream stream, ulong type) =>
        Fixed(stream, type, sizeof(uint)) is { } value ? BinaryPrimitives.ReadUInt32BigEndian(value.Span) : null;

    public static ushort? U16(PureTlvStream stream, ulong type) =>
        Fixed(stream, type, sizeof(ushort)) is { } value ? BinaryPrimitives.ReadUInt16BigEndian(value.Span) : null;

    public static ulong? Tu64(PureTlvStream stream, ulong type)
    {
        if (stream.Get(type) is not { } value)
            return null;
        if (!TruncatedInt.TryDecodeTu64(value.Span, out var result))
            throw new FormatException($"TLV type {type} is not a minimal tu64.");

        return result;
    }

    public static uint? Tu32(PureTlvStream stream, ulong type)
    {
        if (stream.Get(type) is not { } value)
            return null;
        if (!TruncatedInt.TryDecodeTu32(value.Span, out var result))
            throw new FormatException($"TLV type {type} is not a minimal tu32.");

        return result;
    }

    public static CompactPubKey ToPoint(ReadOnlySpan<byte> value, ulong type)
    {
        if (value.Length != CryptoConstants.CompactPubkeyLen || (value[0] != 0x02 && value[0] != 0x03))
            throw new FormatException($"TLV type {type} is not a compressed point.");

        return new CompactPubKey(value.ToArray());
    }

    public static PureTlvRecord U32Record(ulong type, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new PureTlvRecord(type, bytes);
    }

    public static PureTlvRecord U16Record(ulong type, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return new PureTlvRecord(type, bytes);
    }

    public static PureTlvRecord Tu64Record(ulong type, ulong value) => new(type, TruncatedInt.EncodeTu64(value));

    public static PureTlvRecord Tu32Record(ulong type, uint value) => new(type, TruncatedInt.EncodeTu32(value));

    public static FormatException Missing(ulong type) => new($"The required TLV type {type} is missing.");
}