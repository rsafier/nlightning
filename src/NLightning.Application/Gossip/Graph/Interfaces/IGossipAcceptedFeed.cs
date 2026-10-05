namespace NLightning.Application.Gossip.Graph.Interfaces;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;

/// <summary>
/// The slots of the gossip the ingress accepted (NL-366): what the relay's collect drains to find what changed since
/// its last pass, instead of diffing the whole graph snapshot. One slot per message kind (a channel's announcement, an
/// update direction, a node), so the newest accepted version of a slot is the only one collected; the version itself
/// is read from the graph at the drain. Our own gossip never appears here (it goes through the own path), and a
/// message the ingress refused, orphaned or rate-limited is only added when it is actually applied.
/// </summary>
public interface IGossipAcceptedFeed
{
    /// <summary>
    /// Takes what was accepted since the last call. Bounded: when more than the bound waited, the oldest slots were
    /// dropped and <see cref="GossipAcceptedBatch.Overflowed"/> says so, so the collector can fall back to one full
    /// pass over the snapshot, which finds everything.
    /// </summary>
    GossipAcceptedBatch Take();
}

/// <summary>One drain of an <see cref="IGossipAcceptedFeed"/>.</summary>
/// <param name="Slots">The accepted slots, oldest first, one per changed message kind.</param>
/// <param name="Overflowed">True when slots were dropped for the bound since the last drain.</param>
public readonly record struct GossipAcceptedBatch(IReadOnlyList<GossipAcceptedKey> Slots, bool Overflowed);

/// <summary>A changed gossip slot: which message kind, and of what channel or node.</summary>
/// <param name="Type">The message kind (256, 257 or 258).</param>
/// <param name="ShortChannelId">The channel (256, 258), else 0.</param>
/// <param name="Direction">The update's direction (258), else 0.</param>
/// <param name="NodeId">The node (257), else default.</param>
public readonly record struct GossipAcceptedKey(
    MessageTypes Type,
    ShortChannelId ShortChannelId,
    byte Direction,
    CompactPubKey NodeId)
{
    /// <summary>The slot of a <c>channel_announcement</c>.</summary>
    public static GossipAcceptedKey ChannelAnnouncement(ShortChannelId shortChannelId) =>
        new(MessageTypes.ChannelAnnouncement, shortChannelId, 0, default);

    /// <summary>The slot of a <c>channel_update</c> direction.</summary>
    public static GossipAcceptedKey ChannelUpdate(ShortChannelId shortChannelId, byte direction) =>
        new(MessageTypes.ChannelUpdate, shortChannelId, direction, default);

    /// <summary>The slot of a <c>node_announcement</c>.</summary>
    public static GossipAcceptedKey NodeAnnouncement(CompactPubKey nodeId) =>
        new(MessageTypes.NodeAnnouncement, default, 0, nodeId);

    /// <summary>The slot of a <c>channel_announcement_2</c> (NL-878).</summary>
    public static GossipAcceptedKey ChannelAnnouncement2(ShortChannelId shortChannelId) =>
        new(MessageTypes.ChannelAnnouncement2, shortChannelId, 0, default);

    /// <summary>The slot of a <c>channel_update_2</c> direction (NL-878).</summary>
    public static GossipAcceptedKey ChannelUpdate2(ShortChannelId shortChannelId, byte direction) =>
        new(MessageTypes.ChannelUpdate2, shortChannelId, direction, default);

    /// <summary>The slot of a <c>node_announcement_2</c> (NL-878).</summary>
    public static GossipAcceptedKey NodeAnnouncement2(CompactPubKey nodeId) =>
        new(MessageTypes.NodeAnnouncement2, default, 0, nodeId);
}