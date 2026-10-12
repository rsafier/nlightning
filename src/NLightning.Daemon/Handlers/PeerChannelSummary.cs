namespace NLightning.Daemon.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A peer's channels as the operator commands see them (<c>listpeers</c>, <c>disconnect</c>, <c>shutdown</c>): the
/// ones that are not Closed or Stale, and the HTLCs in flight on them.
/// </summary>
/// <remarks>
/// <see cref="HtlcsInFlight"/> counts only the HTLCs that can still be settled over the link
/// (<see cref="ChannelHtlcs.InFlightOffChain"/>): the ones of a channel that resolves on chain (Failed,
/// OnchainResolving) are <see cref="HtlcsResolvingOnChain"/> and never refuse a <c>disconnect</c> or a
/// <c>shutdown</c> (NL-1006); <c>listpeers</c> shows both.
/// </remarks>
internal readonly record struct PeerChannelSummary(int ChannelCount, int HtlcsInFlight)
{
    /// <summary>How many of the counted channels carry HTLCs (NL-595); 0 when none does.</summary>
    public int ChannelsWithHtlcs { get; init; }

    /// <summary>The HTLCs of the counted channels that resolve on chain, not in <see cref="HtlcsInFlight"/>.</summary>
    public int HtlcsResolvingOnChain { get; init; }

    public static PeerChannelSummary For(IChannelMemoryRepository channelMemoryRepository, CompactPubKey peerId)
    {
        ArgumentNullException.ThrowIfNull(channelMemoryRepository);

        return Of(channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerId && IsActive(c)));
    }

    /// <summary>Every channel of the node that is not Closed or Stale, with their HTLCs in flight (<c>shutdown</c>).</summary>
    public static PeerChannelSummary ForAll(IChannelMemoryRepository channelMemoryRepository)
    {
        ArgumentNullException.ThrowIfNull(channelMemoryRepository);

        return Of(channelMemoryRepository.FindChannels(IsActive));
    }

    private static PeerChannelSummary Of(IReadOnlyCollection<ChannelModel> channels) =>
        new(channels.Count, channels.Sum(CountHtlcsInFlight))
        {
            ChannelsWithHtlcs = channels.Count(c => CountHtlcsInFlight(c) > 0),
            HtlcsResolvingOnChain = channels.Where(ChannelHtlcs.ResolvesOnChain).Sum(ChannelHtlcs.InFlight)
        };

    private static bool IsActive(ChannelModel channel) =>
        channel.State is not (ChannelState.Closed or ChannelState.Stale);

    /// <summary>
    /// HTLCs offered or received and not yet removed from both commitments irrevocably (the engine snapshot), or the
    /// legacy in-memory collections of a channel without one, on a channel that does not resolve on chain
    /// (<see cref="ChannelHtlcs.InFlightOffChain"/>, shared with the shutdown drain, NL-592, NL-1006).
    /// </summary>
    internal static int CountHtlcsInFlight(ChannelModel channel) => ChannelHtlcs.InFlightOffChain(channel);
}