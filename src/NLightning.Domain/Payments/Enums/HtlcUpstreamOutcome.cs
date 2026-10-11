namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// What was told upstream about our offered HTLC (the <c>update_fulfill_htlc</c> or <c>update_fail_htlc</c> that
/// settled the incoming HTLC of a forward, or our payment's outcome): derived from the one-way state the switch
/// keeps (<see cref="ForwardCircuitStatus"/>, the incoming HTLC's removal, the payment), never stored itself.
/// </summary>
public enum HtlcUpstreamOutcome : byte
{
    /// <summary>The upstream is not settled yet, or what settled it cannot be told any more.</summary>
    Unknown = 0,

    /// <summary>The upstream was fulfilled: we held the preimage and sent it (or the payment succeeded).</summary>
    Fulfilled = 1,

    /// <summary>The upstream was failed: the incoming HTLC was removed with a fail (a timeout at reasonable depth
    /// included) or the payment failed. Final: an HTLC failed upstream can never be fulfilled.</summary>
    Failed = 2
}