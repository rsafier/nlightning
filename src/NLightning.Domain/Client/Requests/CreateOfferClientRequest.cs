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

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); empty for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}