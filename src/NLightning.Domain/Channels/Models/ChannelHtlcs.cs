namespace NLightning.Domain.Channels.Models;

using Commitments;

/// <summary>
/// HTLC counting rules over a <see cref="ChannelModel"/>, shared by <c>PeerChannelSummary</c>
/// (<c>listpeers</c>, <c>disconnect</c>) and the shutdown drain (<c>shutdown --wait</c>, NL-592).
/// </summary>
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

    /// <summary>The channel's HTLCs in flight, as <see cref="InFlight"/> counts them.</summary>
    public static IEnumerable<HtlcRecord> InFlightRecords(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Commitments is not { } commitments)
            return [];

        return commitments.Htlcs.Values.Where(h => !HtlcStateTable.IsFinal(h.State));
    }
}