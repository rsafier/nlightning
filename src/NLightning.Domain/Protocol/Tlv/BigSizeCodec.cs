using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Tlv;

/// <summary>
/// Span-based BOLT 1 <c>bigsize</c> encoder and strict (canonical) decoder, for the synchronous onion code paths
/// (failure messages) that do not go through the stream-based value-object serializers.
/// </summary>
internal static class BigSizeCodec
{
    /// <summary>
    /// Gets the minimal encoded length of <paramref name="value"/> (1, 3, 5 or 9 bytes).
    /// </summary>
    public static int GetLength(ulong value)
    {
        return value switch
        {
            < 0xfd => 1,
            <= ushort.MaxValue => 3,
            <= uint.MaxValue => 5,
            _ => 9
        };
    }

    /// <summary>
    /// Writes the minimal encoding of <paramref name="value"/> at the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">If <paramref name="destination"/> is too short.</exception>
    public static int Write(ulong value, Span<byte> destination)
    {
        var length = GetLength(value);
        if (destination.Length < length)
            throw new ArgumentException($"Destination needs {length} bytes, got {destination.Length}.",
                                        nameof(destination));

        switch (length)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 3:
                destination[0] = 0xfd;
                BinaryPrimitives.WriteUInt16BigEndian(destination[1..], (ushort)value);
                break;
            case 5:
                destination[0] = 0xfe;
                BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)value);
                break;
            default:
                destination[0] = 0xff;
                BinaryPrimitives.WriteUInt64BigEndian(destination[1..], value);
                break;
        }

        return length;
    }

    /// <summary>
    /// Reads a bigsize from the start of <paramref name="data"/>.
    /// </summary>
    /// <returns><c>false</c> when the data is truncated or the value is not minimally encoded.</returns>
    public static bool TryRead(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;

        if (data.IsEmpty)
            return false;

        switch (data[0])
        {
            case < 0xfd:
                value = data[0];
                length = 1;
                return true;
            case 0xfd:
                if (data.Length < 3)
                    return false;

                value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
                length = 3;
                return value >= 0xfd;
            case 0xfe:
                if (data.Length < 5)
                    return false;

                value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4));
                length = 5;
                return value > ushort.MaxValue;
            default:
                if (data.Length < 9)
                    return false;

                value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(1, 8));
                length = 9;
                return value > uint.MaxValue;
        }
    }
}