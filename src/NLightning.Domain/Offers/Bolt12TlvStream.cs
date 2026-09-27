using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers;

using Protocol.Tlv;

/// <summary>
/// A BOLT 12 TLV stream (offer, invoice_request or invoice) kept as raw records in wire order (BOLT 12 plan §3.5).
/// </summary>
/// <remarks>
/// <para>Raw records let the copy rules (an invoice_request copies every offer field, an invoice every non-signature
/// invoice_request field, unknown odd ones included: BOLT 12 "Invoice Requests" and "Invoices" writers) and the
/// "exactly match" reader rules compare bytes, and let the Merkle tree hash exactly what was received (BOLT 12
/// "Signature Calculation").</para>
/// <para>This is the BOLT 1 layer only. The typed views (<see cref="Offer"/>, <see cref="InvoiceRequest"/>,
/// <see cref="Bolt12Invoice"/>, <see cref="InvoiceError"/>) apply each message's ranges and known types.</para>
/// </remarks>
public sealed class Bolt12TlvStream
{
    /// <summary>
    /// The records, in strictly increasing type order.
    /// </summary>
    public IReadOnlyList<Bolt12TlvRecord> Records { get; }

    /// <summary>
    /// The number of records.
    /// </summary>
    public int Count => Records.Count;

    /// <param name="records">The records, in strictly increasing type order (BOLT 1).</param>
    /// <exception cref="ArgumentException">The types are not strictly increasing.</exception>
    public Bolt12TlvStream(IReadOnlyList<Bolt12TlvRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        for (var i = 1; i < records.Count; i++)
            if (records[i].Type <= records[i - 1].Type)
                throw new ArgumentException("TLV types must be strictly increasing.", nameof(records));

        Records = [.. records];
    }

    /// <summary>
    /// Reads a TLV stream: strictly increasing types, minimal BigSize types and lengths, lengths within the bytes
    /// (BOLT 1). Types are not range-checked here; the BOLT 12 validators do that per message.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a valid TLV stream.</exception>
    public static Bolt12TlvStream Parse(ReadOnlyMemory<byte> bytes) =>
        TryParse(bytes, out var stream, out var reason) ? stream : throw new FormatException(reason);

    /// <summary>
    /// <see cref="Parse"/> without the exception.
    /// </summary>
    /// <returns>False when the bytes are not a valid TLV stream.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out Bolt12TlvStream? stream) =>
        TryParse(bytes, out stream, out _);

    /// <summary>
    /// <see cref="Parse"/> without the exception, with the reason of a refusal.
    /// </summary>
    /// <param name="bytes">The stream's bytes (empty is an empty stream).</param>
    /// <param name="stream">The records; their values share memory with <paramref name="bytes"/>.</param>
    /// <param name="reason">Why the bytes are not a valid TLV stream.</param>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out Bolt12TlvStream? stream,
                                [NotNullWhen(false)] out string? reason)
    {
        stream = null;
        var records = new List<Bolt12TlvRecord>();
        var span = bytes.Span;
        var offset = 0;
        ulong? previousType = null;

        while (offset < span.Length)
        {
            if (!BigSizeCodec.TryRead(span[offset..], out var type, out var typeLength))
            {
                reason = $"TLV type at offset {offset} is truncated or not minimally encoded.";
                return false;
            }

            if (previousType is { } previous && type <= previous)
            {
                reason = $"TLV type {type} at offset {offset} does not follow type {previous} in increasing order.";
                return false;
            }

            offset += typeLength;
            if (!BigSizeCodec.TryRead(span[offset..], out var length, out var lengthLength))
            {
                reason = $"TLV {type} length at offset {offset} is truncated or not minimally encoded.";
                return false;
            }

            offset += lengthLength;
            if (length > (ulong)(span.Length - offset))
            {
                reason = $"TLV {type} length {length} runs past the end ({span.Length - offset} bytes left).";
                return false;
            }

            records.Add(new Bolt12TlvRecord(type, bytes.Slice(offset, (int)length)));
            offset += (int)length;
            previousType = type;
        }

        stream = new Bolt12TlvStream(records);
        reason = null;
        return true;
    }

    /// <summary>
    /// The stream's wire bytes: each record as BigSize type, BigSize length and value, in order.
    /// </summary>
    public byte[] Encode()
    {
        var bytes = new byte[Records.Sum(GetEncodedLength)];
        var offset = 0;
        foreach (var record in Records)
            offset += WriteRecord(record, bytes.AsSpan(offset));

        return bytes;
    }

    /// <summary>
    /// The full encoding of one record (BigSize type, BigSize length, value), as a Merkle leaf hashes it.
    /// </summary>
    public static byte[] EncodeRecord(Bolt12TlvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var bytes = new byte[GetEncodedLength(record)];
        WriteRecord(record, bytes);
        return bytes;
    }

    /// <summary>
    /// The encoded length of <paramref name="record"/>.
    /// </summary>
    public static int GetEncodedLength(Bolt12TlvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return BigSizeCodec.GetLength(record.Type) + BigSizeCodec.GetLength((ulong)record.Value.Length)
             + record.Value.Length;
    }

    /// <summary>
    /// The value of the record of type <paramref name="type"/>, if present.
    /// </summary>
    public bool TryGetValue(ulong type, out ReadOnlyMemory<byte> value)
    {
        foreach (var record in Records)
        {
            if (record.Type == type)
            {
                value = record.Value;
                return true;
            }

            if (record.Type > type)
                break;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Whether the stream has a record of type <paramref name="type"/>.
    /// </summary>
    public bool Contains(ulong type) => TryGetValue(type, out _);

    /// <summary>
    /// A stream with only the records whose type <paramref name="keep"/> accepts, in the same order (for example the
    /// offer fields of an invoice_request, or every field outside the signature range).
    /// </summary>
    public Bolt12TlvStream Filter(Func<ulong, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        return new Bolt12TlvStream(Records.Where(r => keep(r.Type)).ToList());
    }

    /// <summary>
    /// Whether both streams hold the same records with byte-identical values (the BOLT 12 "exactly match" rules).
    /// </summary>
    public bool ContentEquals(Bolt12TlvStream other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Records.Count != Records.Count)
            return false;

        for (var i = 0; i < Records.Count; i++)
        {
            if (Records[i].Type != other.Records[i].Type
             || !Records[i].Value.Span.SequenceEqual(other.Records[i].Value.Span))
                return false;
        }

        return true;
    }

    private static int WriteRecord(Bolt12TlvRecord record, Span<byte> destination)
    {
        var offset = BigSizeCodec.Write(record.Type, destination);
        offset += BigSizeCodec.Write((ulong)record.Value.Length, destination[offset..]);
        record.Value.Span.CopyTo(destination[offset..]);
        return offset + record.Value.Length;
    }
}