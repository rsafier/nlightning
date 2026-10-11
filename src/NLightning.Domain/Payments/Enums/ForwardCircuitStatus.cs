namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Where a forward (incoming HTLC to outgoing HTLC) stands. Values are persisted; never renumber them.
/// </summary>
public enum ForwardCircuitStatus : byte
{
    /// <summary>
    /// The incoming HTLC is locked in and the forward was decided, but the outgoing HTLC has no id yet.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The outgoing HTLC was offered (its add is persisted with this circuit as its origin).
    /// </summary>
    Offered = 1,

    /// <summary>
    /// The downstream peer revealed the preimage; the incoming HTLC is (or must be) fulfilled. Final.
    /// </summary>
    Fulfilled = 2,

    /// <summary>
    /// The outgoing HTLC failed irrevocably, or could not be offered; the incoming HTLC is (or must be) failed.
    /// Final.
    /// </summary>
    Failed = 3
}