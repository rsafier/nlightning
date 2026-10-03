using System.Buffers.Binary;

namespace NLightning.Infrastructure.Bitcoin.Onion;

/// <summary>
/// Minimal BOLT 1 BigSize encoder and canonical decoder for the hop payload length prefix.
/// </summary>
/// <remarks>
/// NLightning.Infrastructure.Bitcoin cannot reference the serialization layer, so the Sphinx core frames the payload
/// length itself.
/// </remarks>
internal static class SphinxBigSize
{
    /// <summary>
    /// Returns the encoded length of <paramref name="value"/> (1, 3, 5 or 9 bytes).
    /// </summary>
    public static int GetEncodedLength(ulong value) => value switch
    {
        < 0xFD => 1,
        <= 0xFFFF => 3,
        <= 0xFFFFFFFF => 5,
        _ => 9
    };

    /// <summary>
    /// Writes <paramref name="value"/> at the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">If the destination is too short.</exception>
    public static int Write(ulong value, Span<byte> destination)
    {
        var length = GetEncodedLength(value);
        if (destination.Length < length)
            throw new ArgumentException("Destination is too short for the BigSize value.", nameof(destination));

        switch (length)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 3:
                destination[0] = 0xFD;
                BinaryPrimitives.WriteUInt16BigEndian(destination[1..], (ushort)value);
                break;
            case 5:
                destination[0] = 0xFE;
                BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)value);
                break;
            default:
                destination[0] = 0xFF;
                BinaryPrimitives.WriteUInt64BigEndian(destination[1..], value);
                break;
        }

        return length;
    }

    /// <summary>
    /// Reads a canonical (minimally encoded) BigSize from the start of <paramref name="source"/>.
    /// </summary>
    /// <returns><c>false</c> if the source is too short or the encoding is not minimal.</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;

        if (source.IsEmpty)
            return false;

        var prefix = source[0];
        switch (prefix)
        {
            case < 0xFD:
                value = prefix;
                bytesRead = 1;
                return true;
            case 0xFD:
                if (source.Length < 3)
                    return false;

                value = BinaryPrimitives.ReadUInt16BigEndian(source[1..]);
                bytesRead = 3;
                return value >= 0xFD;
            case 0xFE:
                if (source.Length < 5)
                    return false;

                value = BinaryPrimitives.ReadUInt32BigEndian(source[1..]);
                bytesRead = 5;
                return value > 0xFFFF;
            default:
                if (source.Length < 9)
                    return false;

                value = BinaryPrimitives.ReadUInt64BigEndian(source[1..]);
                bytesRead = 9;
                return value > 0xFFFFFFFF;
        }
    }
}