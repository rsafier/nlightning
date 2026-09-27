// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Crypto.ValueObjects;

/// <summary>
/// A BOLT 12 offer we created (<c>OfferModel</c>, BOLT 12 plan §3.10, migration <c>AddBolt12Offers</c>), keyed by
/// offer id.
/// </summary>
/// <remarks>
/// Offers are never deleted: the BOLT 12 invoices we issued for one (<c>Invoices.OfferId</c>) reference it.
/// </remarks>
public class OfferEntity
{
    /// <summary>
    /// SHA256 of <see cref="OfferBytes"/>.
    /// </summary>
    /// <remarks>This is the primary key</remarks>
    public required Hash OfferId { get; set; }

    /// <summary>
    /// The <c>lno1...</c> string.
    /// </summary>
    public required string Bolt12 { get; set; }

    /// <summary>
    /// The offer's TLV stream, exactly as encoded in <see cref="Bolt12"/>.
    /// </summary>
    public required byte[] OfferBytes { get; set; }

    /// <summary>
    /// <c>offer_description</c>, if any.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// <c>offer_amount</c> in millisatoshi, or null for an any-amount offer.
    /// </summary>
    public long? AmountMsat { get; set; }

    /// <summary>
    /// <c>offer_currency</c> (ISO 4217), or null for msat.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// <c>offer_issuer</c>, if any.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// <c>offer_quantity_max</c>: null for a single item, 0 for unlimited.
    /// </summary>
    public ulong? QuantityMax { get; set; }

    /// <summary>
    /// <c>offer_absolute_expiry</c> (stored as UTC ticks), or null when the offer does not expire.
    /// </summary>
    public DateTimeOffset? AbsoluteExpiry { get; set; }

    /// <summary>
    /// <c>offer_metadata</c>.
    /// </summary>
    public required byte[] Metadata { get; set; }

    /// <summary>
    /// <c>OfferIssuerKind</c> (0 node id, 1 blinded paths).
    /// </summary>
    public required byte IssuerKind { get; set; }

    /// <summary>
    /// Whether the offer carries <c>offer_paths</c>.
    /// </summary>
    public required bool HasPaths { get; set; }

    /// <summary>
    /// <c>OfferStatus</c> (0 active, 1 disabled, 2 expired).
    /// </summary>
    public required byte Status { get; set; }

    /// <summary>
    /// When the offer was created (stored as UTC ticks).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the offer was disabled (stored as UTC ticks), if it was.
    /// </summary>
    public DateTimeOffset? DisabledAt { get; set; }

    // Default constructor for EF Core
    internal OfferEntity()
    {
    }
}