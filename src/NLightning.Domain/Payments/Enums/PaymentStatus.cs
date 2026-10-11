namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Where one of our outgoing payments stands. Values are persisted; never renumber them.
/// </summary>
public enum PaymentStatus : byte
{
    /// <summary>
    /// The HTLC is offered (or about to be) and not resolved.
    /// </summary>
    InFlight = 0,

    /// <summary>
    /// The payee revealed the preimage. Final.
    /// </summary>
    Succeeded = 1,

    /// <summary>
    /// The HTLC failed irrevocably, or it could not be offered. Final.
    /// </summary>
    Failed = 2
}