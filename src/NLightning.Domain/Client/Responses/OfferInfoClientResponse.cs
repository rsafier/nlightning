namespace NLightning.Domain.Client.Responses;

using Accounting.Labels;
using Crypto.ValueObjects;
using Money;
using Offers.Enums;
using Offers.Models;

/// <summary>
/// One of our BOLT 12 offers, as returned by <c>createoffer</c>, <c>listoffers</c> and <c>disableoffer</c>.
/// </summary>
public sealed class OfferInfoClientResponse
{
    /// <summary>SHA256 of the offer's TLV stream.</summary>
    public required Hash OfferId { get; init; }

    /// <summary>The <c>lno1...</c> string.</summary>
    public required string Bolt12 { get; init; }

    public string? Description { get; init; }

    /// <summary>The amount per item, or null for any amount.</summary>
    public LightningMoney? Amount { get; init; }

    public string? Issuer { get; init; }

    /// <summary>null for a single item, 0 for unlimited.</summary>
    public ulong? QuantityMax { get; init; }

    public DateTimeOffset? AbsoluteExpiry { get; init; }

    /// <summary>Whether the offer carries <c>offer_paths</c>.</summary>
    public bool HasPaths { get; init; }

    public required OfferStatus Status { get; init; }

    /// <summary>Whether the offer still answered invoice_requests when the response was built (active and not
    /// expired).</summary>
    public bool IsActive { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DisabledAt { get; init; }

    /// <summary>Settled invoices issued for the offer, when counted.</summary>
    public int? PaidInvoices { get; init; }

    /// <summary>Open, unexpired invoices issued for the offer, when counted.</summary>
    public int? UnpaidInvoices { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1), or null.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c>, sorted by key (NL-602 A3-T1); empty for none.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Maps a stored offer; <paramref name="now"/> decides <see cref="IsActive"/>.
    /// </summary>
    public static OfferInfoClientResponse FromModel(OfferModel offer, DateTimeOffset now,
                                                    OfferInvoiceCounts? counts = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return new OfferInfoClientResponse
        {
            OfferId = offer.OfferId,
            Bolt12 = offer.Bolt12,
            Description = offer.Description,
            Amount = offer.Amount,
            Issuer = offer.Issuer,
            QuantityMax = offer.QuantityMax,
            AbsoluteExpiry = offer.AbsoluteExpiry,
            HasPaths = offer.HasPaths,
            Status = offer.Status,
            IsActive = offer.IsActive(now),
            CreatedAt = offer.CreatedAt,
            DisabledAt = offer.DisabledAt,
            PaidInvoices = counts?.Paid,
            UnpaidInvoices = counts?.Unpaid,
            Label = offer.Label,
            Tags = SourceLabels.FromStored(null, offer.Tags).TagStrings
        };
    }
}