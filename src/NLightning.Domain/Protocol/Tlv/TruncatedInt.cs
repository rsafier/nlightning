using System.Buffers.Binary;
using System.Numerics;

namespace NLightning.Domain.Protocol.Tlv;

/// <summary>
/// BOLT 1 truncated unsigned integers (<c>tu16</c>, <c>tu32</c>, <c>tu64</c>).
/// </summary>
/// <remarks>
/// A truncated integer is the big-endian encoding of the value with all leading zero bytes omitted, so zero is encoded
/// as an empty byte sequence. Decoding is strict: an encoding with a leading zero byte, or longer than the width of the
/// target type, is rejected with a <see cref="FormatException"/>.
/// </remarks>
public static class TruncatedInt
{
    public const int MaxTu16Length = sizeof(ushort);
    public const int MaxTu32Length = sizeof(uint);
    public const int MaxTu64Length = sizeof(ulong);

    /// <summary>
    /// Gets the number of bytes needed to encode <paramref name="value"/> as a truncated integer.
    /// </summary>
    public static int GetEncodedLength(ulong value)
    {
        return value == 0 ? 0 : sizeof(ulong) - (BitOperations.LeadingZeroCount(value) / 8);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a <c>tu16</c>.
    /// </summary>
    public static byte[] EncodeTu16(ushort value) => Encode(value);

    /// <summary>
    /// Encodes <paramref name="value"/> as a <c>tu32</c>.
    /// </summary>
    public static byte[] EncodeTu32(uint value) => Encode(value);

    /// <summary>
    /// Encodes <paramref name="value"/> as a <c>tu64</c>.
    /// </summary>
    public static byte[] EncodeTu64(ulong value) => Encode(value);

    /// <summary>
    /// Writes the minimal big-endian encoding of <paramref name="value"/> into <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written (0 for a zero value).</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small.</exception>
    public static int Encode(ulong value, Span<byte> destination)
    {
        var length = GetEncodedLength(value);
        if (destination.Length < length)
            throw new ArgumentException($"Destination must be at least {length} bytes long.", nameof(destination));

        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        buffer[(sizeof(ulong) - length)..].CopyTo(destination);

        return length;
    }

    /// <summary>
    /// Decodes a strict <c>tu16</c>.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the encoding is longer than 2 bytes or not minimal.</exception>
    public static ushort DecodeTu16(ReadOnlySpan<byte> bytes) => (ushort)Decode(bytes, MaxTu16Length, "tu16");

    /// <summary>
    /// Decodes a strict <c>tu32</c>.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the encoding is longer than 4 bytes or not minimal.</exception>
    public static uint DecodeTu32(ReadOnlySpan<byte> bytes) => (uint)Decode(bytes, MaxTu32Length, "tu32");

    /// <summary>
    /// Decodes a strict <c>tu64</c>.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the encoding is longer than 8 bytes or not minimal.</exception>
    public static ulong DecodeTu64(ReadOnlySpan<byte> bytes) => Decode(bytes, MaxTu64Length, "tu64");

    /// <summary>
    /// Tries to decode a strict <c>tu16</c>.
    /// </summary>
    public static bool TryDecodeTu16(ReadOnlySpan<byte> bytes, out ushort value)
    {
        var ok = TryDecode(bytes, MaxTu16Length, out var result);
        value = (ushort)result;
        return ok;
    }

    /// <summary>
    /// Tries to decode a strict <c>tu32</c>.
    /// </summary>
    public static bool TryDecodeTu32(ReadOnlySpan<byte> bytes, out uint value)
    {
        var ok = TryDecode(bytes, MaxTu32Length, out var result);
        value = (uint)result;
        return ok;
    }

    /// <summary>
    /// Tries to decode a strict <c>tu64</c>.
    /// </summary>
    public static bool TryDecodeTu64(ReadOnlySpan<byte> bytes, out ulong value)
    {
        return TryDecode(bytes, MaxTu64Length, out value);
    }

    private static byte[] Encode(ulong value)
    {
        var bytes = new byte[GetEncodedLength(value)];
        Encode(value, bytes);
        return bytes;
    }

    private static ulong Decode(ReadOnlySpan<byte> bytes, int maxLength, string typeName)
    {
        if (bytes.Length > maxLength)
            throw new FormatException($"{typeName} encoding is longer than {maxLength} bytes.");

        if (bytes.Length > 0 && bytes[0] == 0)
            throw new FormatException($"{typeName} encoding is not minimal.");

        return ReadBigEndian(bytes);
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, int maxLength, out ulong value)
    {
        value = 0;
        if (bytes.Length > maxLength || (bytes.Length > 0 && bytes[0] == 0))
            return false;

        value = ReadBigEndian(bytes);
        return true;
    }

    private static ulong ReadBigEndian(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (var b in bytes)
            value = (value << 8) | b;

        return value;
    }
}