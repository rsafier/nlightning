namespace NLightning.Domain.Offers;

using Constants;
using Crypto.ValueObjects;
using Encoding;
using Protocol.OnionMessages;
using Protocol.ValueObjects;

/// <summary>
/// The typed offer fields (types 2-22) of an offer, invoice_request or invoice. Null means absent; unknown odd fields
/// stay in the message's raw stream.
/// </summary>
public sealed record OfferFields
{
    /// <summary>
    /// <c>offer_chains</c>; empty when present with no entry (invalid for a reader, B12-OFR-03).
    /// </summary>
    public IReadOnlyList<ChainHash>? Chains { get; init; }

    /// <summary>
    /// <c>offer_metadata</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? Metadata { get; init; }

    /// <summary>
    /// <c>offer_currency</c> (ISO 4217), or null for the chain's own unit (msat).
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// <c>offer_amount</c>: msat, or the currency's minor unit when <see cref="Currency"/> is set.
    /// </summary>
    public ulong? Amount { get; init; }

    /// <summary>
    /// <c>offer_description</c>.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// <c>offer_features</c> (big-endian bitmap as on the wire).
    /// </summary>
    public ReadOnlyMemory<byte>? Features { get; init; }

    /// <summary>
    /// <c>offer_absolute_expiry</c>, seconds since the epoch.
    /// </summary>
    public ulong? AbsoluteExpiry { get; init; }

    /// <summary>
    /// <c>offer_paths</c>.
    /// </summary>
    public IReadOnlyList<WireBlindedPath>? Paths { get; init; }

    /// <summary>
    /// <c>offer_issuer</c>.
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>
    /// <c>offer_quantity_max</c> (0 means unlimited).
    /// </summary>
    public ulong? QuantityMax { get; init; }

    /// <summary>
    /// <c>offer_issuer_id</c>.
    /// </summary>
    public CompactPubKey? IssuerId { get; init; }

    /// <summary>
    /// Whether the message responds to (or is) an offer: <c>offer_issuer_id</c> or <c>offer_paths</c> is set (BOLT 12
    /// invoice_request reader).
    /// </summary>
    public bool IsOfferResponse => IssuerId is not null || Paths is not null;

    /// <summary>
    /// Reads the offer fields of <paramref name="stream"/>; types are already checked by the caller.
    /// </summary>
    internal static OfferFields Read(Bolt12TlvStream stream, string zeroHopsRequirementId)
    {
        var fields = new OfferFields();
        foreach (var record in stream.Records)
        {
            fields = record.Type switch
            {
                Bolt12TlvTypes.OfferChains => fields with { Chains = Bolt12FieldCodec.ReadChains(record) },
                Bolt12TlvTypes.OfferMetadata => fields with { Metadata = record.Value.ToArray() },
                Bolt12TlvTypes.OfferCurrency => fields with { Currency = Bolt12FieldCodec.ReadUtf8(record) },
                Bolt12TlvTypes.OfferAmount => fields with { Amount = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.OfferDescription => fields with { Description = Bolt12FieldCodec.ReadUtf8(record) },
                Bolt12TlvTypes.OfferFeatures => fields with { Features = record.Value.ToArray() },
                Bolt12TlvTypes.OfferAbsoluteExpiry => fields with { AbsoluteExpiry = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.OfferPaths => fields with
                {
                    Paths = Bolt12FieldCodec.ReadPaths(record, zeroHopsRequirementId)
                },
                Bolt12TlvTypes.OfferIssuer => fields with { Issuer = Bolt12FieldCodec.ReadUtf8(record) },
                Bolt12TlvTypes.OfferQuantityMax => fields with { QuantityMax = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.OfferIssuerId => fields with { IssuerId = Bolt12FieldCodec.ReadPoint(record) },
                _ => fields
            };
        }

        return fields;
    }
}