using System.Buffers.Binary;
using System.Numerics;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

/// <summary>
/// Minimal truncated-integer (tu16/tu32/tu64) codec for the onion TLV converters (BOLT 1).
/// </summary>
/// <remarks>
/// Encoding is big-endian with all leading zero bytes removed (zero encodes to zero bytes). Decoding is strict: it
/// rejects a leading zero byte (non-minimal encoding) and values longer than the integer width.
/// TODO: unify with the shared TruncatedInt helper once it lands.
/// </remarks>
internal static class OnionTruncatedInt
{
    public const int Tu32MaxLength = sizeof(uint);
    public const int Tu64MaxLength = sizeof(ulong);

    public static byte[] Encode(ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);

        return buffer[(BitOperations.LeadingZeroCount(value) / 8)..].ToArray();
    }

    public static bool TryDecodeTu64(ReadOnlySpan<byte> bytes, out ulong value) =>
        TryDecode(bytes, Tu64MaxLength, out value);

    public static bool TryDecodeTu32(ReadOnlySpan<byte> bytes, out uint value)
    {
        if (!TryDecode(bytes, Tu32MaxLength, out var decoded))
        {
            value = 0;
            return false;
        }

        value = (uint)decoded;
        return true;
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, int maxLength, out ulong value)
    {
        value = 0;

        if (bytes.Length > maxLength)
            return false;

        // Minimal encoding: no leading zero byte
        if (bytes.Length > 0 && bytes[0] == 0)
            return false;

        foreach (var b in bytes)
            value = (value << 8) | b;

        return true;
    }
}