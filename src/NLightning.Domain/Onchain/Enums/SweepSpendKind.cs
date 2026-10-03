namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// How one input of a sweep, claim or penalty spends its output: the witness form, the <c>nSequence</c>/<c>nLockTime</c>
/// rules and the key (<see cref="SweepSpendKinds.GetKeyKind"/>). BOLT 3 §Commitment Transaction Outputs and BOLT 5.
/// Persisted later as a byte; never renumber.
/// </summary>
public enum SweepSpendKind : byte
{
    /// <summary>
    /// Our <c>to_local</c> or the output of our HTLC-timeout/success transaction, after <c>to_self_delay</c>:
    /// <c>&lt;local_delayedsig&gt; &lt;&gt;</c>, <c>nSequence = to_self_delay</c> (B5-LCL-01, B5-LCL-LO-03).
    /// </summary>
    DelayedOutput = 1,

    /// <summary>
    /// Our <c>to_remote</c> on a peer commitment: P2WPKH <c>&lt;sig&gt; &lt;payment_basepoint&gt;</c>; with anchors the
    /// P2WSH <c>&lt;remotepubkey&gt; OP_CHECKSIGVERIFY 1 OP_CSV</c> spent with <c>&lt;sig&gt;</c> and
    /// <c>nSequence = 1</c> (B5-RMT-02, D5).
    /// </summary>
    PaymentToRemote = 2,

    /// <summary>
    /// An HTLC we offered, on the peer's commitment (a "received" output there), after it timed out:
    /// <c>&lt;localhtlcsig&gt; &lt;&gt;</c>, <c>nLockTime = cltv_expiry</c> (B5-RMT-LO-02).
    /// </summary>
    HtlcTimeoutClaim = 3,

    /// <summary>
    /// An HTLC the peer offered, on its commitment (an "offered" output there), with the preimage:
    /// <c>&lt;localhtlcsig&gt; &lt;payment_preimage&gt;</c> (B5-RMT-RO-01).
    /// </summary>
    HtlcPreimageClaim = 4,

    /// <summary>
    /// The peer's <c>to_local</c> on a revoked commitment, or the output of its HTLC-timeout/success transaction spending
    /// a revoked commitment: <c>&lt;revocation_sig&gt; 1</c> (B5-REV-03, B5-REV-06).
    /// </summary>
    RevokedDelayedOutput = 5,

    /// <summary>
    /// An HTLC output of a revoked commitment: <c>&lt;revocation_sig&gt; &lt;revocationpubkey&gt;</c>
    /// (B5-REV-04, B5-REV-05).
    /// </summary>
    RevokedHtlc = 6
}

/// <summary>
/// Helpers for <see cref="SweepSpendKind"/>.
/// </summary>
public static class SweepSpendKinds
{
    /// <summary>The key that signs an input spent this way.</summary>
    public static SweepKeyKind GetKeyKind(this SweepSpendKind kind) => kind switch
    {
        SweepSpendKind.DelayedOutput => SweepKeyKind.DelayedPayment,
        SweepSpendKind.PaymentToRemote => SweepKeyKind.Payment,
        SweepSpendKind.HtlcTimeoutClaim or SweepSpendKind.HtlcPreimageClaim => SweepKeyKind.HtlcRemotePoint,
        SweepSpendKind.RevokedDelayedOutput or SweepSpendKind.RevokedHtlc => SweepKeyKind.Revocation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>True for the penalty forms (revocation key).</summary>
    public static bool IsPenalty(this SweepSpendKind kind) =>
        kind is SweepSpendKind.RevokedDelayedOutput or SweepSpendKind.RevokedHtlc;
}