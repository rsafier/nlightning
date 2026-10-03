namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// The only events that move an HTLC (or a fee update) through <see cref="HtlcStateTable"/>.
/// </summary>
public enum HtlcEvent : byte
{
    /// <summary>We sent <c>commitment_signed</c> (we signed the peer's commitment).</summary>
    SendCommit = 0,

    /// <summary>We received <c>commitment_signed</c> (a new local commitment).</summary>
    RecvCommit = 1,

    /// <summary>We sent <c>revoke_and_ack</c> (we revoked our previous commitment).</summary>
    SendRevoke = 2,

    /// <summary>We received <c>revoke_and_ack</c> (the peer revoked its previous commitment).</summary>
    RecvRevoke = 3,

    /// <summary>We sent <c>update_fulfill_htlc</c>, <c>update_fail_htlc</c> or <c>update_fail_malformed_htlc</c>.</summary>
    SendRemove = 4,

    /// <summary>We received <c>update_fulfill_htlc</c>, <c>update_fail_htlc</c> or <c>update_fail_malformed_htlc</c>.</summary>
    RecvRemove = 5,
}