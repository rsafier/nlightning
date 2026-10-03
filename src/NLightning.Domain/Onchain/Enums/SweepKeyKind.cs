namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// Which channel key signs one input of a sweep, claim or penalty (BOLT 5 plan §3.5). The signer derives the private
/// key from the kind; the caller never sees it. Persisted later as a byte; never renumber.
/// </summary>
public enum SweepKeyKind : byte
{
    /// <summary>
    /// <c>local_delayedprivkey = delayed_payment_basepoint_secret + SHA256(per_commitment_point ||
    /// delayed_payment_basepoint)</c>, with the point of our commitment: our <c>to_local</c> and the outputs of our
    /// HTLC-timeout/success transactions (B5-LCL-01, B5-LCL-LO-03).
    /// </summary>
    DelayedPayment = 1,

    /// <summary>
    /// <c>payment_basepoint_secret</c> itself (option_static_remotekey): our <c>to_remote</c> on a peer commitment
    /// (B5-RMT-02, deviation D5).
    /// </summary>
    Payment = 2,

    /// <summary>
    /// <c>htlc_basepoint_secret + SHA256(per_commitment_point || htlc_basepoint)</c> with the point of the <b>peer's</b>
    /// commitment: direct HTLC claims on its commitment (B5-RMT-LO-02, B5-RMT-RO-01).
    /// </summary>
    HtlcRemotePoint = 3,

    /// <summary>
    /// <c>revocationprivkey = revocation_basepoint_secret * SHA256(revocation_basepoint || per_commitment_point) +
    /// per_commitment_secret * SHA256(per_commitment_point || revocation_basepoint)</c> with the peer's revealed secret:
    /// penalties (B5-REV-03..06).
    /// </summary>
    Revocation = 4
}