namespace NLightning.Domain.Offers.Models;

using Crypto.ValueObjects;
using Enums;
using Money;

/// <summary>
/// An offer we created (BOLT 12 "Offers", <c>createoffer</c>): its encoded bytes and string and the fields the
/// invoice_request reader checks against.
/// </summary>
/// <remarks>
/// <para><see cref="OfferBytes"/> are the offer's TLV stream exactly as encoded in <see cref="Bolt12"/>; an
/// invoice_request is for this offer when its offer fields (types 1-79 and 1,000,000,000-1,999,999,999) are exactly
/// these bytes (BOLT 12 "Invoice Requests" reader, B12-IRQ-03). <see cref="OfferId"/> is SHA256 of
/// <see cref="OfferBytes"/> (internal only).</para>
/// <para>Status moves only from <see cref="OfferStatus.Active"/> to <see cref="OfferStatus.Disabled"/> or
/// <see cref="OfferStatus.Expired"/>; the mutators throw <see cref="InvalidOperationException"/> otherwise.</para>
/// </remarks>
public sealed class OfferModel
{
    /// <summary>
    /// SHA256 of <see cref="OfferBytes"/>.
    /// </summary>
    public Hash OfferId { get; }

    /// <summary>
    /// The <c>lno1...</c> string handed to payers.
    /// </summary>
    public string Bolt12 { get; }

    /// <summary>
    /// The offer's TLV stream.
    /// </summary>
    public ReadOnlyMemory<byte> OfferBytes { get; }

    /// <summary>
    /// <c>offer_description</c>; required when <see cref="Amount"/> is set.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// <c>offer_amount</c> in msat, or null for an offer that lets the payer choose (we never create offers in
    /// another <c>offer_currency</c>).
    /// </summary>
    public LightningMoney? Amount { get; }

    /// <summary>
    /// <c>offer_currency</c> (ISO 4217), or null for msat. Always null for offers we create; kept for completeness.
    /// </summary>
    public string? Currency { get; }

    /// <summary>
    /// <c>offer_issuer</c>, or null.
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// <c>offer_quantity_max</c>: null for a single item, 0 for unlimited, otherwise the most items per request.
    /// </summary>
    public ulong? QuantityMax { get; }

    /// <summary>
    /// <c>offer_absolute_expiry</c>, or null when the offer does not expire.
    /// </summary>
    public DateTimeOffset? AbsoluteExpiry { get; }

    /// <summary>
    /// <c>offer_metadata</c>: random bytes that make the offer unique.
    /// </summary>
    public ReadOnlyMemory<byte> Metadata { get; }

    /// <summary>
    /// Which key signs the offer's invoices.
    /// </summary>
    public OfferIssuerKind IssuerKind { get; }

    /// <summary>
    /// Whether the offer carries <c>offer_paths</c> (then an invoice_request must arrive through one of them, B12-IRQ-03).
    /// </summary>
    public bool HasPaths { get; }

    public DateTimeOffset CreatedAt { get; }

    public OfferStatus Status { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    public OfferModel(Hash offerId, string bolt12, ReadOnlyMemory<byte> offerBytes, string? description,
                      LightningMoney? amount, string? currency, string? issuer, ulong? quantityMax,
                      DateTimeOffset? absoluteExpiry, ReadOnlyMemory<byte> metadata, OfferIssuerKind issuerKind,
                      bool hasPaths, DateTimeOffset createdAt, OfferStatus status = OfferStatus.Active,
                      DateTimeOffset? disabledAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt12);
        if (offerBytes.IsEmpty)
            throw new ArgumentException("The offer bytes are required.", nameof(offerBytes));
        if (amount is { IsZero: true })
            throw new ArgumentOutOfRangeException(nameof(amount), "An offer amount must be positive; use null for "
                                                                + "any amount.");
        if (amount is not null && string.IsNullOrEmpty(description))
            throw new ArgumentException("An offer with an amount needs a description.", nameof(description));
        if (currency is not null && amount is null)
            throw new ArgumentException("An offer with a currency needs an amount.", nameof(currency));
        if (!Enum.IsDefined(issuerKind))
            throw new ArgumentOutOfRangeException(nameof(issuerKind), issuerKind, "Unknown issuer kind.");
        if (issuerKind == OfferIssuerKind.BlindedPaths && !hasPaths)
            throw new ArgumentException("An offer without an issuer id needs paths.", nameof(hasPaths));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown offer status.");
        if (status == OfferStatus.Disabled && disabledAt is null)
            throw new ArgumentException("A disabled offer needs its disable time.", nameof(disabledAt));

        OfferId = offerId;
        Bolt12 = bolt12;
        OfferBytes = offerBytes;
        Description = description;
        Amount = amount;
        Currency = currency;
        Issuer = issuer;
        QuantityMax = quantityMax;
        AbsoluteExpiry = absoluteExpiry;
        Metadata = metadata;
        IssuerKind = issuerKind;
        HasPaths = hasPaths;
        CreatedAt = createdAt;
        Status = status;
        DisabledAt = disabledAt;
    }

    /// <summary>
    /// True when <paramref name="now"/> is at or past <see cref="AbsoluteExpiry"/> (BOLT 12: an expired offer is not
    /// answered), whatever the stored <see cref="Status"/>.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) => AbsoluteExpiry is { } expiry && now >= expiry;

    /// <summary>
    /// Whether an invoice_request for this offer is answered at <paramref name="now"/>.
    /// </summary>
    public bool IsActive(DateTimeOffset now) => Status == OfferStatus.Active && !IsExpired(now);

    /// <summary>
    /// Disables an active offer.
    /// </summary>
    public void Disable(DateTimeOffset disabledAt)
    {
        if (Status != OfferStatus.Active)
            throw new InvalidOperationException($"Cannot disable an offer that is {Status}.");

        DisabledAt = disabledAt;
        Status = OfferStatus.Disabled;
    }

    /// <summary>
    /// Marks an active offer past its <see cref="AbsoluteExpiry"/> as expired.
    /// </summary>
    public void MarkExpired(DateTimeOffset now)
    {
        if (Status != OfferStatus.Active)
            throw new InvalidOperationException($"Cannot expire an offer that is {Status}.");
        if (!IsExpired(now))
            throw new InvalidOperationException("The offer has not expired yet.");

        Status = OfferStatus.Expired;
    }
}