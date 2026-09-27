using System.Buffers.Binary;

namespace NLightning.Domain.Payments.Keysend;

/// <summary>
/// Checks custom records and stores them as a BOLT 1 TLV stream (<c>bigsize type || bigsize length || value</c>, types
/// strictly increasing, canonical bigsizes).
/// </summary>
public static class CustomRecordCodec
{
    /// <summary>
    /// The first custom-record type (65536, LND's <c>record.CustomTypeStart</c>).
    /// </summary>
    public const ulong MinType = 65536;

    /// <summary>
    /// The keysend preimage record (5482373484). It carries the payment's preimage, so it is never a custom record a
    /// caller may set.
    /// </summary>
    public const ulong KeysendPreimageType = 5482373484;

    /// <summary>
    /// Returns the records sorted by type.
    /// </summary>
    /// <exception cref="ArgumentException">A type is below <see cref="MinType"/>, is
    /// <see cref="KeysendPreimageType"/>, or appears twice.</exception>
    public static IReadOnlyList<CustomRecord> Validate(IEnumerable<CustomRecord>? records)
    {
        if (records is null)
            return [];

        var sorted = new List<CustomRecord>();
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(records));
            if (record.Type < MinType)
                throw new ArgumentException($"Custom record type {record.Type} is below {MinType}.", nameof(records));
            if (record.Type == KeysendPreimageType)
                throw new ArgumentException($"Custom record type {KeysendPreimageType} is the keysend preimage.",
                                            nameof(records));

            sorted.Add(record);
        }

        sorted.Sort((a, b) => a.Type.CompareTo(b.Type));
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Type == sorted[i - 1].Type)
                throw new ArgumentException($"Custom record type {sorted[i].Type} appears twice.", nameof(records));
        }

        return sorted;
    }

    /// <summary>
    /// Encodes <paramref name="records"/> (checked by <see cref="Validate"/>) as a TLV stream.
    /// </summary>
    public static byte[] Encode(IEnumerable<CustomRecord>? records)
    {
        var sorted = Validate(records);
        using var stream = new MemoryStream();
        foreach (var record in sorted)
        {
            WriteBigSize(stream, record.Type);
            WriteBigSize(stream, (ulong)record.Value.Length);
            stream.Write(record.Value.Span);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Decodes a TLV stream written by <see cref="Encode"/>.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a canonical TLV stream of custom records.</exception>
    public static IReadOnlyList<CustomRecord> Decode(ReadOnlySpan<byte> bytes)
    {
        var records = new List<CustomRecord>();
        var offset = 0;
        ulong? previous = null;
        while (offset < bytes.Length)
        {
            var type = ReadBigSize(bytes, ref offset);
            var length = ReadBigSize(bytes, ref offset);
            if (length > (ulong)(bytes.Length - offset))
                throw new FormatException($"Custom record {type} is longer than the bytes left.");
            if (previous is { } last && type <= last)
                throw new FormatException($"Custom record type {type} is not above {last}.");
            if (type < MinType)
                throw new FormatException($"Custom record type {type} is below {MinType}.");

            records.Add(new CustomRecord(type, bytes.Slice(offset, (int)length)));
            offset += (int)length;
            previous = type;
        }

        return records;
    }

    private static void WriteBigSize(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        int length;
        switch (value)
        {
            case < 0xfd:
                buffer[0] = (byte)value;
                length = 1;
                break;
            case <= ushort.MaxValue:
                buffer[0] = 0xfd;
                BinaryPrimitives.WriteUInt16BigEndian(buffer[1..], (ushort)value);
                length = 3;
                break;
            case <= uint.MaxValue:
                buffer[0] = 0xfe;
                BinaryPrimitives.WriteUInt32BigEndian(buffer[1..], (uint)value);
                length = 5;
                break;
            default:
                buffer[0] = 0xff;
                BinaryPrimitives.WriteUInt64BigEndian(buffer[1..], value);
                length = 9;
                break;
        }

        stream.Write(buffer[..length]);
    }

    private static ulong ReadBigSize(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset >= bytes.Length)
            throw new FormatException("Truncated bigsize.");

        var prefix = bytes[offset++];
        var (size, min) = prefix switch
        {
            0xfd => (2, 0xfdUL),
            0xfe => (4, 0x10000UL),
            0xff => (8, 0x100000000UL),
            _ => (0, 0UL)
        };
        if (size == 0)
            return prefix;

        if (bytes.Length - offset < size)
            throw new FormatException("Truncated bigsize.");

        var slice = bytes.Slice(offset, size);
        offset += size;
        var value = size switch
        {
            2 => BinaryPrimitives.ReadUInt16BigEndian(slice),
            4 => BinaryPrimitives.ReadUInt32BigEndian(slice),
            _ => BinaryPrimitives.ReadUInt64BigEndian(slice)
        };
        if (value < min)
            throw new FormatException("Non-canonical bigsize.");

        return value;
    }
}