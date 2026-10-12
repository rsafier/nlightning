namespace NLightning.Domain.Channels.Models;

using Commitments;
using Enums;

/// <summary>
/// HTLC counting rules over a <see cref="ChannelModel"/>, shared by <c>PeerChannelSummary</c>
/// (<c>listpeers</c>, <c>disconnect</c>) and the shutdown drain (<c>shutdown --wait</c>, NL-592).
/// </summary>
/// <remarks>
/// The HTLCs of a channel that <see cref="ResolvesOnChain">resolves on chain</see> are still in flight
/// (<see cref="InFlight"/>) but can no longer be settled over the link: <c>disconnect</c> and <c>shutdown</c> count
/// only <see cref="InFlightOffChain"/> (NL-1006).
/// </remarks>
public static class ChannelHtlcs
{
    /// <summary>
    /// HTLCs offered or received and not yet removed from both commitments irrevocably (the engine snapshot), or the
    /// legacy in-memory collections of a channel without one.
    /// </summary>
    public static int InFlight(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Commitments is { } commitments)
            return commitments.Htlcs.Values.Count(h => !HtlcStateTable.IsFinal(h.State));

        return (channel.LocalOfferedHtlcs?.Count ?? 0) + (channel.RemoteOfferedHtlcs?.Count ?? 0);
    }

    /// <summary>
    /// Whether the channel takes no update any more and its HTLCs are resolved on chain: Failed (our commitment
    /// broadcast, or the peer's awaited) or OnchainResolving. The expiry monitor and the BOLT 5 resolvers take them up
    /// again after a restart, so waiting for the link or the peer cannot settle them (NL-1006).
    /// </summary>
    public static bool ResolvesOnChain(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.State is ChannelState.Failed or ChannelState.OnchainResolving;
    }

    /// <summary>
    /// The HTLCs in flight that can still be settled over the link: <see cref="InFlight"/>, or 0 for a channel that
    /// <see cref="ResolvesOnChain">resolves on chain</see> (NL-1006).
    /// </summary>
    public static int InFlightOffChain(ChannelModel channel) => ResolvesOnChain(channel) ? 0 : InFlight(channel);

    /// <summary>The channel's HTLCs in flight, as <see cref="InFlight"/> counts them.</summary>
    public static IEnumerable<HtlcRecord> InFlightRecords(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Commitments is not { } commitments)
            return [];

        return commitments.Htlcs.Values.Where(h => !HtlcStateTable.IsFinal(h.State));
    }
}