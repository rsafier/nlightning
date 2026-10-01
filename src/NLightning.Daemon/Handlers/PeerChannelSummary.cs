namespace NLightning.Daemon.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A peer's channels as the operator commands see them (<c>listpeers</c>, <c>disconnect</c>, <c>shutdown</c>): the
/// ones that are not Closed or Stale, and the HTLCs in flight on them.
/// </summary>
internal readonly record struct PeerChannelSummary(int ChannelCount, int HtlcsInFlight)
{
    /// <summary>How many of the counted channels carry HTLCs (NL-595); 0 when none does.</summary>
    public int ChannelsWithHtlcs { get; init; }

    public static PeerChannelSummary For(IChannelMemoryRepository channelMemoryRepository, CompactPubKey peerId)
    {
        ArgumentNullException.ThrowIfNull(channelMemoryRepository);

        var channels = channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerId && IsActive(c));
        return new PeerChannelSummary(channels.Count, channels.Sum(CountHtlcsInFlight))
        {
            ChannelsWithHtlcs = channels.Count(c => CountHtlcsInFlight(c) > 0)
        };
    }

    /// <summary>Every channel of the node that is not Closed or Stale, with their HTLCs in flight (<c>shutdown</c>).</summary>
    public static PeerChannelSummary ForAll(IChannelMemoryRepository channelMemoryRepository)
    {
        ArgumentNullException.ThrowIfNull(channelMemoryRepository);

        var channels = channelMemoryRepository.FindChannels(IsActive);
        return new PeerChannelSummary(channels.Count, channels.Sum(CountHtlcsInFlight))
        {
            ChannelsWithHtlcs = channels.Count(c => CountHtlcsInFlight(c) > 0)
        };
    }

    private static bool IsActive(ChannelModel channel) =>
        channel.State is not (ChannelState.Closed or ChannelState.Stale);

    /// <summary>
    /// HTLCs offered or received and not yet removed from both commitments irrevocably (the engine snapshot), or the
    /// legacy in-memory collections of a channel without one (<see cref="ChannelHtlcs.InFlight"/>, shared with the
    /// shutdown drain, NL-592).
    /// </summary>
    internal static int CountHtlcsInFlight(ChannelModel channel) => ChannelHtlcs.InFlight(channel);
}