namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The switch events of an HTLC we offered that was resolved on a peer commitment (BOLT 5 plan §3.4, O3-T4): the same
/// <see cref="OutgoingHtlcFulfilled"/>/<see cref="OutgoingHtlcFailed"/> the commitment engine raises off chain, so the
/// switch fulfills or fails the upstream HTLC (or the local payment) exactly as it does for an off-chain removal.
/// </summary>
public static class RemoteHtlcSwitchEvents
{
    /// <summary>
    /// <c>HtlcRemovalKind.OnchainTimeout = 4</c> of BOLT 5 plan O3-T4 (no reason bytes: the switch creates
    /// <c>permanent_channel_failure</c> as the erring node).
    /// </summary>
    public const HtlcRemovalKind OnchainTimeoutKind = HtlcRemovalKind.OnchainTimeout;

    /// <summary>The preimage of our offered HTLC is known (seen on chain or learned off chain): fulfill upstream at
    /// once (B5-RMT-LO-01).</summary>
    public static OutgoingHtlcFulfilled Fulfilled(ChannelId channelId, ulong htlcId, Hash paymentHash,
                                                  Secret preimage) =>
        new(channelId, htlcId, paymentHash, preimage);

    /// <summary>Our offered HTLC was settled on chain without a preimage (our timeout claim, or no output) and that is
    /// reasonably deep: fail upstream (B5-RMT-LO-02, B5-RMT-LO-03).</summary>
    public static OutgoingHtlcFailed OnchainTimeout(ChannelId channelId, ulong htlcId, Hash paymentHash) =>
        new(channelId, htlcId, paymentHash, new HtlcRemoval(OnchainTimeoutKind));
}