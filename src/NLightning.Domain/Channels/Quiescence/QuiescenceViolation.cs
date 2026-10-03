namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// A <c>stfu</c> or update from the peer that breaks BOLT 2 "Channel Quiescence". Each one is answered with a
/// <c>warning</c> for the channel and the connection is closed (<see cref="QuiescenceRules.CreateWarning"/>); the
/// channel is never failed (the disconnection itself ends the quiescence, Q-R-04).
/// </summary>
public enum QuiescenceViolation : byte
{
    /// <summary>A <c>stfu</c> although <c>option_quiesce</c> is not negotiated (Q-S-01).</summary>
    NotNegotiated = 1,

    /// <summary>A second <c>stfu</c> on this connection ("MUST NOT send <c>stfu</c> twice", Q-S-03).</summary>
    SecondStfu = 2,

    /// <summary>
    /// A <c>stfu</c> with <c>initiator</c> = 0 although we sent none: it replies to nothing ("if it is replying to an
    /// <c>stfu</c>: MUST set <c>initiator</c> to 0; otherwise: MUST set <c>initiator</c> to 1", Q-S-03).
    /// </summary>
    UnsolicitedReply = 3,

    /// <summary>
    /// An update message (<c>update_add_htlc</c>, <c>update_fulfill_htlc</c>, <c>update_fail_htlc</c>,
    /// <c>update_fail_malformed_htlc</c>, <c>update_fee</c>) after the peer's <c>stfu</c> ("MUST NOT send an update
    /// message after <c>stfu</c>", Q-S-04).
    /// </summary>
    UpdateAfterStfu = 4
}