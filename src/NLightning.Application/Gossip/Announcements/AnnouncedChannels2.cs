using System.Collections.Concurrent;

namespace NLightning.Application.Gossip.Announcements;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Payloads;

/// <summary>
/// Our channels announced with a complete, signed <c>channel_announcement_2</c> in this process (taproot gossip,
/// NL-878), shared by the announcement, <c>channel_update_2</c> and <c>node_announcement_2</c> paths.
/// </summary>
/// <remarks>
/// Memory only: after a restart a channel is announced again by a fresh MuSig2 session (our
/// <c>channel_reestablish</c> sets <c>my_current_funding_locked</c> bit 1 with fresh nonces), never by a stored nonce
/// or partial signature, while the graph keeps serving the announcement it already has.
/// </remarks>
public sealed class AnnouncedChannels2
{
    private readonly ConcurrentDictionary<ChannelId, ChannelAnnouncement2Payload> _announced = new();

    /// <summary>Whether the channel has a complete <c>channel_announcement_2</c> in this process.</summary>
    public bool IsAnnounced(ChannelId channelId) => _announced.ContainsKey(channelId);

    /// <summary>The channel's complete announcement, if any.</summary>
    public ChannelAnnouncement2Payload? Get(ChannelId channelId) => _announced.GetValueOrDefault(channelId);

    /// <summary>Whether any channel of ours is announced with v2 gossip.</summary>
    public bool Any => !_announced.IsEmpty;

    /// <summary>The funding block of our oldest v2-announced channel (the floor of our node_announcement_2).</summary>
    public uint? OldestFundingHeight =>
        _announced.IsEmpty ? null : _announced.Values.Min(a => a.ShortChannelId.BlockHeight);

    /// <summary>Records a complete announcement; returns false when the same one was recorded already.</summary>
    public bool Set(ChannelId channelId, ChannelAnnouncement2Payload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var previous = _announced.GetValueOrDefault(channelId);
        _announced[channelId] = announcement;
        return previous is null || previous.ShortChannelId != announcement.ShortChannelId;
    }

    /// <summary>Forgets the channel (its funding moved, or it closed).</summary>
    public void Remove(ChannelId channelId) => _announced.TryRemove(channelId, out _);
}