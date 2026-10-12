namespace NLightning.Domain.Offers.Enums;

/// <summary>
/// Whether one of our offers still answers invoice_requests. Values are persisted; never renumber them.
/// </summary>
public enum OfferStatus : byte
{
    /// <summary>
    /// Answers invoice_requests until its <c>offer_absolute_expiry</c>, if any.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Disabled by the operator (<c>disableoffer</c>): invoice_requests for it are no longer answered. Final.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// Past its <c>offer_absolute_expiry</c> (BOLT 12: a reader MUST NOT respond to an expired offer). Final.
    /// </summary>
    Expired = 2
}