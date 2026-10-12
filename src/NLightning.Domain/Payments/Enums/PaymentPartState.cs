namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Where one part of a split payment stands. Values are persisted (table <c>PaymentParts</c>); never renumber them.
/// </summary>
public enum PaymentPartState : byte
{
    /// <summary>
    /// The HTLC was offered and its outcome is not known yet.
    /// </summary>
    InFlight = 0,

    /// <summary>
    /// The payee revealed the preimage for this part. Final.
    /// </summary>
    Succeeded = 1,

    /// <summary>
    /// The part failed irrevocably (or the node restart proved its HTLC gone, with the outcome unknown). Final.
    /// </summary>
    Failed = 2
}