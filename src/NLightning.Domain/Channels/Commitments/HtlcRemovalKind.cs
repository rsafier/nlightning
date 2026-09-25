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
}