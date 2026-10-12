namespace NLightning.Domain.Offers;

using Crypto.ValueObjects;
using Encoding;
using Models;
using Protocol.Onion.Models;
using Protocol.OnionMessages;
using Protocol.Tlv;
using Protocol.ValueObjects;

/// <summary>
/// Builds a <see cref="Bolt12TlvStream"/> for a writer (our offers, invoice_requests, invoices): typed setters over
/// raw records, sorted by type on <see cref="Build"/>.
/// </summary>
/// <remarks>
/// Copy rules start from a received stream: <c>new Bolt12TlvStreamBuilder(offer.Stream)</c> keeps every record,
/// unknown ones included (BOLT 12: an invoice_request copies every offer field, an invoice every non-signature
/// invoice_request field). Setting a type again replaces its value. Nothing is validated here; parse the result
/// with the typed view (<see cref="Offer"/>, <see cref="InvoiceRequest"/>, <see cref="Bolt12Invoice"/>) and run its
/// validator.
/// </remarks>
public sealed class Bolt12TlvStreamBuilder
{
    private readonly SortedDictionary<ulong, ReadOnlyMemory<byte>> _records = new();

    public Bolt12TlvStreamBuilder()
    {
    }

    /// <summary>
    /// Starts from the records of <paramref name="stream"/> whose type <paramref name="keep"/> accepts (all when
    /// null).
    /// </summary>
    public Bolt12TlvStreamBuilder(Bolt12TlvStream stream, Func<ulong, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        foreach (var record in stream.Records)
            if (keep is null || keep(record.Type))
                _records[record.Type] = record.Value.ToArray();
    }

    /// <summary>
    /// Sets the raw value of <paramref name="type"/>.
    /// </summary>
    public Bolt12TlvStreamBuilder Set(ulong type, ReadOnlySpan<byte> value)
    {
        _records[type] = value.ToArray();
        return this;
    }

    /// <summary>
    /// Sets a <c>tu64</c> (minimal big-endian, empty for 0).
    /// </summary>
    public Bolt12TlvStreamBuilder SetTu64(ulong type, ulong value) => Set(type, TruncatedInt.EncodeTu64(value));

    /// <summary>
    /// Sets a <c>tu32</c>.
    /// </summary>
    public Bolt12TlvStreamBuilder SetTu32(ulong type, uint value) => Set(type, TruncatedInt.EncodeTu32(value));

    /// <summary>
    /// Sets a UTF-8 string.
    /// </summary>
    public Bolt12TlvStreamBuilder SetUtf8(ulong type, string value) =>
        Set(type, Bolt12FieldCodec.EncodeUtf8(value));

    /// <summary>
    /// Sets a 33-byte <c>point</c>.
    /// </summary>
    public Bolt12TlvStreamBuilder SetPoint(ulong type, CompactPubKey point) => Set(type, point);

    /// <summary>
    /// Sets a <c>chain_hash*</c> list (<c>offer_chains</c>) or, with one entry, a <c>chain_hash</c>
    /// (<c>invreq_chain</c>).
    /// </summary>
    public Bolt12TlvStreamBuilder SetChains(ulong type, IReadOnlyList<ChainHash> chains) =>
        Set(type, Bolt12FieldCodec.EncodeChains(chains));

    /// <summary>
    /// Sets a <c>blinded_path*</c> list.
    /// </summary>
    public Bolt12TlvStreamBuilder SetPaths(ulong type, IReadOnlyList<WireBlindedPath> paths) =>
        Set(type, BlindedPathCodec.EncodeList(paths));

    /// <summary>
    /// Sets a <c>blinded_payinfo*</c> list (<c>invoice_blindedpay</c>).
    /// </summary>
    public Bolt12TlvStreamBuilder SetPayInfos(ulong type, IReadOnlyList<BlindedPayInfo> payInfos) =>
        Set(type, Bolt12FieldCodec.EncodePayInfos(payInfos));

    /// <summary>
    /// Sets a <c>fallback_address*</c> list (<c>invoice_fallbacks</c>).
    /// </summary>
    public Bolt12TlvStreamBuilder SetFallbacks(ulong type, IReadOnlyList<FallbackAddress> fallbacks) =>
        Set(type, Bolt12FieldCodec.EncodeFallbacks(fallbacks));

    /// <summary>
    /// Sets an <c>invreq_bip_353_name</c>.
    /// </summary>
    public Bolt12TlvStreamBuilder SetBip353Name(ulong type, Bip353Name name) =>
        Set(type, Bolt12FieldCodec.EncodeBip353Name(name));

    /// <summary>
    /// Removes <paramref name="type"/> if present.
    /// </summary>
    public Bolt12TlvStreamBuilder Remove(ulong type)
    {
        _records.Remove(type);
        return this;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is set.
    /// </summary>
    public bool Contains(ulong type) => _records.ContainsKey(type);

    /// <summary>
    /// The stream, in increasing type order.
    /// </summary>
    public Bolt12TlvStream Build() =>
        new(_records.Select(r => new Bolt12TlvRecord(r.Key, r.Value)).ToList());
}