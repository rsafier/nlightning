namespace NLightning.Domain.Payments.Trampoline;

/// <summary>
/// Where a trampoline relay stands (NL-875). Persisted as a byte; never renumber.
/// </summary>
/// <remarks>
/// Moves only forward: Collecting → Sending → Fulfilled | Failed, or Collecting → Failed (the incoming set never
/// completed, or the relay was refused before anything went out).
/// </remarks>
public enum TrampolineRelayStatus : byte
{
    /// <summary>
    /// Incoming parts are being collected (the outer onion's <c>total_msat</c> is not reached yet); nothing was sent.
    /// </summary>
    Collecting = 0,

    /// <summary>
    /// The incoming set is complete and the outgoing payment was started: its HTLCs carry
    /// <c>HtlcOrigin.Trampoline(PaymentHash)</c>.
    /// </summary>
    Sending = 1,

    /// <summary>
    /// The outgoing payment learnt the preimage: every incoming part is fulfilled (or carries the preimage on its
    /// record, to be claimed on chain).
    /// </summary>
    Fulfilled = 2,

    /// <summary>
    /// The relay failed for good: every incoming part is failed back once no outgoing HTLC is unresolved.
    /// </summary>
    Failed = 3
}