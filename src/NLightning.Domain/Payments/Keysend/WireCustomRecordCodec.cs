using System.Buffers.Binary;

namespace NLightning.Domain.Payments.Keysend;

/// <summary>
/// The custom records of an <c>update_add_htlc</c> extension (LND's <c>lnwire.CustomRecords</c>, NL-1182): TLV records
/// of type 65536 or more (<see cref="CustomRecordCodec.MinType"/>), stored on the HTLC record as a canonical TLV stream.
/// Unlike a final hop's onion records, any type at or above the minimum is allowed (LND's <c>CustomRecords.Validate</c>).
/// </summary>
public static class WireCustomRecordCodec
{
    /// <summary>
    /// Returns the records sorted by type.
    /// </summary>
    /// <exception cref="ArgumentException">A type is below <see cref="CustomRecordCodec.MinType"/> (LND: "custom records
    /// entry with TLV type below min: 65536") or appears twice.</exception>
    public static IReadOnlyList<CustomRecord> Validate(IEnumerable<CustomRecord>? records)
    {
        if (records is null)
            return [];

        var sorted = new List<CustomRecord>();
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(records));
            if (record.Type < CustomRecordCodec.MinType)
                throw new ArgumentException(
                    $"custom records entry with TLV type below min: {CustomRecordCodec.MinType}", nameof(records));

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
    /// <paramref name="existing"/> with <paramref name="overrides"/> merged in, a record of the same type replaced
    /// (LND's <c>CustomRecords.MergedCopy</c>), sorted by type.
    /// </summary>
    /// <exception cref="ArgumentException">A record is invalid (<see cref="Validate"/>).</exception>
    public static IReadOnlyList<CustomRecord> Merge(IEnumerable<CustomRecord>? existing,
                                                    IEnumerable<CustomRecord>? overrides)
    {
        var merged = new SortedDictionary<ulong, CustomRecord>();
        foreach (var record in Validate(existing))
            merged[record.Type] = record;
        foreach (var record in Validate(overrides))
            merged[record.Type] = record;
        return merged.Values.ToList();
    }

    /// <summary>Encodes <paramref name="records"/> (checked by <see cref="Validate"/>) as a TLV stream; empty for none.</summary>
    public static byte[] Encode(IEnumerable<CustomRecord>? records)
    {
        var sorted = Validate(records);
        if (sorted.Count == 0)
            return [];

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
    /// Decodes a stream written by <see cref="Encode"/>; bytes that are not a canonical stream of custom records give no
    /// records (a stored row never breaks a channel load).
    /// </summary>
    public static IReadOnlyList<CustomRecord> Decode(ReadOnlyMemory<byte> bytes) =>
        bytes.IsEmpty ? [] : CustomRecordCodec.TryDecode(bytes.Span, out var records) ? records : [];

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
}