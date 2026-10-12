namespace NLightning.Domain.Routing.Pathfinding;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Gossip.Graph;

/// <summary>
/// One direction of a channel: the BOLT 7 <c>direction</c> bit is 0 when the sender is the lexicographically lesser
/// node id (<c>node_id_1</c>), 1 otherwise.
/// </summary>
/// <param name="ShortChannelId">The channel.</param>
/// <param name="Direction">0 or 1.</param>
public readonly record struct DirectedChannel(ShortChannelId ShortChannelId, byte Direction)
{
    /// <summary>
    /// The direction of the edge <paramref name="from"/> → <paramref name="to"/> of <paramref name="shortChannelId"/>.
    /// </summary>
    public static DirectedChannel Between(ShortChannelId shortChannelId, CompactPubKey from, CompactPubKey to) =>
        new(shortChannelId, GraphChannel.CompareNodeIds(from, to) < 0 ? (byte)0 : (byte)1);
}