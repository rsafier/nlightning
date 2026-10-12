namespace NLightning.Domain.Offers.Models;

using Accounting.Labels;
using Money;

/// <summary>
/// What <c>IOfferService.CreateOfferAsync</c> puts in a new offer (BOLT 12 "Offers" writer).
/// </summary>
/// <param name="Amount"><c>offer_amount</c> in msat, or null to let the payer choose.</param>
/// <param name="Description"><c>offer_description</c>; required with an amount (B12-OFR-01).</param>
/// <param name="Issuer"><c>offer_issuer</c>, or null.</param>
/// <param name="QuantityMax"><c>offer_quantity_max</c>: null for a single item, 0 for unlimited.</param>
/// <param name="AbsoluteExpiry"><c>offer_absolute_expiry</c>, or null.</param>
/// <param name="ForcePaths">Add <c>offer_paths</c> even when we have a public channel (they are always added when we
/// have none, B12-OFR-02).</param>
public sealed record CreateOfferRequest(LightningMoney? Amount, string? Description, string? Issuer = null,
                                        ulong? QuantityMax = null, DateTimeOffset? AbsoluteExpiry = null,
                                        bool ForcePaths = false)
{
    /// <summary>
    /// The operator's label and tags (NL-602 A3-T1) the offer row carries (and every invoice issued for the offer, so
    /// its <c>InvoiceSettled</c> event); <see cref="SourceLabels.None"/> for none.
    /// </summary>
    public SourceLabels Labels { get; init; } = SourceLabels.None;
}