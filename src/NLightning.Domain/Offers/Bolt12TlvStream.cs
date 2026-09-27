namespace NLightning.Domain.Offers;

/// <summary>
/// A BOLT 12 TLV stream (offer, invoice_request or invoice) kept as raw records in wire order (BOLT 12 plan §3.5).
/// </summary>
/// <remarks>
/// <para>Raw records let the copy rules (an invoice_request copies every offer field, an invoice every non-signature
/// invoice_request field, unknown odd ones included: BOLT 12 "Invoice Requests" and "Invoices" writers) and the
/// "exactly match" reader rules compare bytes, and let the Merkle tree hash exactly what was received (BOLT 12
/// "Signature Calculation").</para>
/// <para>Contract of wave B12 (B12-0): <see cref="Parse"/>, <see cref="TryParse"/> and <see cref="Encode"/> are
/// implemented by lane B12-A (B0-T2).</para>
/// </remarks>
public sealed class Bolt12TlvStream
{
    /// <summary>
    /// The records, in strictly increasing type order.
    /// </summary>
    public IReadOnlyList<Bolt12TlvRecord> Records { get; }

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
        throw new NotImplementedException("Bolt12TlvStream.Parse is implemented by lane B12-A (B0-T2).");

    /// <summary>
    /// <see cref="Parse"/> without the exception.
    /// </summary>
    /// <returns>False when the bytes are not a valid TLV stream.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, out Bolt12TlvStream? stream) =>
        throw new NotImplementedException("Bolt12TlvStream.TryParse is implemented by lane B12-A (B0-T2).");

    /// <summary>
    /// The stream's wire bytes: each record as BigSize type, BigSize length and value, in order.
    /// </summary>
    public byte[] Encode() =>
        throw new NotImplementedException("Bolt12TlvStream.Encode is implemented by lane B12-A (B0-T2).");

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
    /// A stream with only the records whose type <paramref name="keep"/> accepts, in the same order (for example the
    /// offer fields of an invoice_request, or every field outside the signature range).
    /// </summary>
    public Bolt12TlvStream Filter(Func<ulong, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        return new Bolt12TlvStream(Records.Where(r => keep(r.Type)).ToList());
    }
}