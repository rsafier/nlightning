namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// How an output of a commitment or second-level transaction on chain is resolved (BOLT 5 plan §3.3; persisted as a
/// byte in <c>OutputResolutions.Descriptor</c>: never renumber, only append). The descriptor data blob next to it holds
/// what the kind needs (id, expiry, ...); keys are always re-derived, never stored.
/// </summary>
public enum OutputDescriptorKind : byte
{
    /// <summary>Our <c>to_local</c> (or our second-level HTLC output): swept after <c>to_self_delay</c>.</summary>
    DelayedToLocal = 1,

    /// <summary>Our <c>to_remote</c> on the peer's commitment (static_remotekey): swept at once.</summary>
    PaymentToRemote = 2,

    /// <summary>An HTLC we offered, on our commitment: HTLC-timeout at <c>cltv_expiry</c>.</summary>
    LocalOfferedHtlc = 3,

    /// <summary>An HTLC the peer offered, on our commitment: HTLC-success when the preimage is known.</summary>
    LocalReceivedHtlc = 4,

    /// <summary>An HTLC we offered, on the peer's commitment: direct timeout claim at <c>cltv_expiry</c>.</summary>
    RemoteReceivedHtlc = 5,

    /// <summary>An HTLC the peer offered, on the peer's commitment: direct preimage claim.</summary>
    RemoteOfferedHtlc = 6,

    /// <summary>The peer's <c>to_local</c> on a revoked commitment: penalty.</summary>
    RevokedToLocal = 7,

    /// <summary>Any HTLC output of a revoked commitment: penalty.</summary>
    RevokedHtlc = 8,

    /// <summary>The output of the peer's HTLC transaction spending a revoked HTLC output: penalty.</summary>
    RevokedSecondLevel = 9
}