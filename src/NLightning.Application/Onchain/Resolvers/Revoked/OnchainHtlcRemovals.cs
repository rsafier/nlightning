namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Channels.Commitments;

/// <summary>
/// The removal carried by an <c>OutgoingHtlcFailed</c> raised for an HTLC settled on chain without a preimage (BOLT 5
/// plan O3-T4, B5-REV-RES-02/03): no reason bytes, the switch creates <c>permanent_channel_failure</c> as the erring
/// node.
/// </summary>
/// <remarks>
/// <c>HtlcRemovalKind.OnchainTimeout = 4</c> is added by lane W5-B (switch integration); until the lanes are
/// integrated the value is cast here, in one place, so the integrator can switch to the named member.
/// </remarks>
public static class OnchainHtlcRemovals
{
    /// <summary>The persisted value of <c>HtlcRemovalKind.OnchainTimeout</c> (O3-T4).</summary>
    public const byte OnchainTimeoutKind = 4;

    /// <summary>The removal of an HTLC failed because of its on-chain resolution.</summary>
    public static HtlcRemoval OnchainTimeout() => new((HtlcRemovalKind)OnchainTimeoutKind);
}