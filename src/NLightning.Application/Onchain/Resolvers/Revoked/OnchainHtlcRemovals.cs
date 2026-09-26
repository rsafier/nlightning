namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Channels.Commitments;

/// <summary>
/// The removal carried by an <c>OutgoingHtlcFailed</c> raised for an HTLC settled on chain without a preimage (BOLT 5
/// plan O3-T4, B5-REV-RES-02/03): no reason bytes, the switch creates <c>permanent_channel_failure</c> as the erring
/// node.
/// </summary>
public static class OnchainHtlcRemovals
{
    /// <summary>The persisted value of <c>HtlcRemovalKind.OnchainTimeout</c> (O3-T4).</summary>
    public const byte OnchainTimeoutKind = (byte)HtlcRemovalKind.OnchainTimeout;

    /// <summary>The removal of an HTLC failed because of its on-chain resolution.</summary>
    public static HtlcRemoval OnchainTimeout() => HtlcRemoval.OnchainTimeout();
}