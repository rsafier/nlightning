namespace NLightning.Domain.Client.Requests;

using Money;

/// <summary>
/// Creates one of our BOLT 12 offers (<c>createoffer</c>).
/// </summary>
public sealed class CreateOfferClientRequest
{
    /// <summary><c>offer_amount</c>, or null to let the payer choose.</summary>
    public LightningMoney? Amount { get; init; }

    /// <summary><c>offer_description</c>; required with an amount.</summary>
    public string? Description { get; init; }

    /// <summary><c>offer_issuer</c>, or null.</summary>
    public string? Issuer { get; init; }

    /// <summary><c>offer_quantity_max</c>: null for a single item, 0 for unlimited.</summary>
    public ulong? QuantityMax { get; init; }

    /// <summary><c>offer_absolute_expiry</c> in seconds since 1970, or null.</summary>
    public ulong? AbsoluteExpiry { get; init; }

    /// <summary>Add <c>offer_paths</c> even when we have a public channel.</summary>
    public bool ForcePaths { get; init; }
}