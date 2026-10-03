namespace NLightning.Domain.Routing.Pathfinding;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Gossip.Graph;

/// <summary>
/// A directed edge that is not in the graph (our private channel, or a BOLT 11 route hint entry: the hint's node
/// charges <see cref="Policy"/> to forward to <see cref="To"/>).
/// </summary>
/// <param name="From">The sender side.</param>
/// <param name="To">The receiver side.</param>
/// <param name="ShortChannelId">The channel (real or alias).</param>
/// <param name="Policy">The policy of <paramref name="From"/> on this channel.</param>
/// <param name="CapacityMsat">The capacity, when known.</param>
public sealed record ExtraEdge(CompactPubKey From, CompactPubKey To, ShortChannelId ShortChannelId, GraphPolicy Policy,
                               ulong? CapacityMsat = null);