namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Where an invoice we issued stands. Values are persisted; never renumber them.
/// </summary>
/// <remarks>
/// Expiry is not a status: an <see cref="Open"/> invoice past <c>InvoiceModel.ExpiresAt</c> is expired
/// (<c>InvoiceModel.IsExpired</c>) and the final hop treats it as unknown.
/// </remarks>
public enum InvoiceStatus : byte
{
    /// <summary>
    /// Issued and not paid yet.
    /// </summary>
    Open = 0,

    /// <summary>
    /// An HTLC paying it is locked in and our <c>update_fulfill_htlc</c> is persisted, but the removal is not
    /// irrevocably committed yet.
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// Paid: the fulfill of the paying HTLC is irrevocably committed. Final.
    /// </summary>
    Settled = 2,

    /// <summary>
    /// Canceled before it was paid; HTLCs for it fail with <c>incorrect_or_unknown_payment_details</c>. Final.
    /// </summary>
    Canceled = 3,

    /// <summary>
    /// A hold invoice (NL-995) whose paying HTLC set is complete and held: locked in, nothing fulfilled or failed,
    /// waiting for the operator's settle (the preimage arrives from outside) or cancel. The deadline monitor's
    /// fail-back is the guard: a part failed back near its CLTV cancels the hold.
    /// </summary>
    Held = 4
}