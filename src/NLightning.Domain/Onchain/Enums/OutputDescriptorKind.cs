namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// What an output of a commitment or second-level transaction on chain is to us, and so how it is resolved (BOLT 5 plan
/// §3.3; persisted as a byte in <c>OutputResolutions.Descriptor</c>: never renumber, only append). The descriptor data
/// blob next to it holds what the kind needs (id, expiry, ...); keys are always re-derived, never stored.
/// </summary>
public enum OutputDescriptorKind : byte
{
    /// <summary>An output of the transaction that matches none of the expected scripts.</summary>
    Unknown = 0,

    /// <summary>Our <c>to_local</c> on our commitment (or our second-level HTLC output): sweep after
    /// <c>to_self_delay</c> with <c>&lt;local_delayedsig&gt; &lt;&gt;</c> (B5-LCL-01).</summary>
    DelayedToLocal = 1,

    /// <summary>Our <c>to_remote</c> on a peer commitment (P2WPKH to our <c>payment_basepoint</c>, static_remotekey):
    /// swept at once (D5, B5-RMT-02, B5-REV-02).</summary>
    PaymentToRemote = 2,

    /// <summary>An HTLC we offered, on our commitment: HTLC-timeout at <c>cltv_expiry</c>, or the peer's preimage
    /// spend (B5-LCL-LO-*).</summary>
    LocalOfferedHtlc = 3,

    /// <summary>An HTLC the peer offered, on our commitment: HTLC-success with an allowed preimage
    /// (B5-LCL-RO-*).</summary>
    LocalReceivedHtlc = 4,

    /// <summary>An HTLC we offered, on the peer's commitment (a "received" output there): claim with
    /// <c>&lt;sig&gt; &lt;&gt;</c> at <c>cltv_expiry</c> (B5-RMT-LO-*).</summary>
    RemoteReceivedHtlc = 5,

    /// <summary>An HTLC the peer offered, on its commitment (an "offered" output there): claim with
    /// <c>&lt;sig&gt; &lt;preimage&gt;</c> (B5-RMT-RO-*).</summary>
    RemoteOfferedHtlc = 6,

    /// <summary>The peer's <c>to_local</c> on a revoked commitment: penalty <c>&lt;revocation_sig&gt; 1</c>
    /// (B5-REV-03).</summary>
    RevokedToLocal = 7,

    /// <summary>Any HTLC output of a revoked commitment: penalty <c>&lt;revocation_sig&gt; &lt;revocationpubkey&gt;</c>
    /// (B5-REV-04/05).</summary>
    RevokedHtlc = 8,

    /// <summary>The output of the peer's HTLC transaction spending a revoked HTLC output: penalty.</summary>
    RevokedSecondLevel = 9,

    /// <summary>The peer's own balance output (<c>to_remote</c> on our commitment, <c>to_local</c> on its unrevoked
    /// commitment): nothing for us (B5-LCL-02, B5-RMT-02).</summary>
    PeerOutput = 10,

    /// <summary>Our anchor (option_anchors, O7).</summary>
    OurAnchor = 11,

    /// <summary>The peer's anchor (anyone may sweep it after 16 blocks, O7).</summary>
    PeerAnchor = 12
}