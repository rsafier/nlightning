namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// How an HTLC is removed.
/// </summary>
public enum HtlcRemovalKind : byte
{
    /// <summary><c>update_fulfill_htlc</c>: the amount moves to the receiver of the HTLC.</summary>
    Fulfill = 1,

    /// <summary><c>update_fail_htlc</c>: the amount returns to the offerer.</summary>
    Fail = 2,

    /// <summary><c>update_fail_malformed_htlc</c>: the amount returns to the offerer.</summary>
    FailMalformed = 3,

    /// <summary>
    /// Not a wire message: an HTLC we offered was settled on chain without a preimage (our HTLC-timeout or timeout
    /// claim, or a commitment without its output, reasonably deep; BOLT 5 plan O3-T4). It carries no reason bytes: the
    /// switch fails the upstream HTLC with <c>permanent_channel_failure</c> created as the erring node. Never persisted
    /// as an HTLC removal.
    /// </summary>
    OnchainTimeout = 4,
}