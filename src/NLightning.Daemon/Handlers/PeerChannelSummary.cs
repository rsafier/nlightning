namespace NLightning.Daemon.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A peer's channels as the operator commands see them (<c>listpeers</c>, <c>disconnect</c>): the ones that are not
/// Closed or Stale, and the HTLCs in flight on them.
/// </summary>
internal readonly record struct PeerChannelSummary(int ChannelCount, int HtlcsInFlight)
{
    public static PeerChannelSummary For(IChannelMemoryRepository channelMemoryRepository, CompactPubKey peerId)
    {
        ArgumentNullException.ThrowIfNull(channelMemoryRepository);

        var channels = channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerId && IsActive(c));
        return new PeerChannelSummary(channels.Count, channels.Sum(CountHtlcsInFlight));
    }

    private static bool IsActive(ChannelModel channel) =>
        channel.State is not (ChannelState.Closed or ChannelState.Stale);

    /// <summary>
    /// HTLCs offered or received and not yet removed from both commitments irrevocably (the engine snapshot), or the
    /// legacy in-memory collections of a channel without one.
    /// </summary>
    internal static int CountHtlcsInFlight(ChannelModel channel)
    {
        if (channel.Commitments is { } commitments)
            return commitments.Htlcs.Values.Count(h => !HtlcStateTable.IsFinal(h.State));

        return (channel.LocalOfferedHtlcs?.Count ?? 0) + (channel.RemoteOfferedHtlcs?.Count ?? 0);
    }
}