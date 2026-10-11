namespace NLightning.Domain.Gossip.Graph;

/// <summary>
/// The gossip protocols a graph channel or node was announced with (BOLT 7 and taproot gossip, BOLTs PR #1059,
/// NL-878). Persisted as a byte: never renumber.
/// </summary>
/// <remarks>
/// The draft keeps the two protocols disjoint and asks nodes that understand both to keep both advertisements (for
/// syncing with older peers) and to favour the new one for routing: a channel announced with both keeps both raw
/// announcements, its v1 policies (<see cref="GraphChannel.Policy1"/>) and its v2 policies
/// (<see cref="GraphChannel.Policy1V2"/>), and routing reads <see cref="GraphChannel.GetRoutingPolicy"/>.
/// </remarks>
[Flags]
public enum GraphGossipVersions : byte
{
    /// <summary>Not announced (never stored).</summary>
    None = 0,

    /// <summary>BOLT 7 <c>channel_announcement</c>/<c>node_announcement</c>.</summary>
    V1 = 1,

    /// <summary>Taproot gossip <c>channel_announcement_2</c>/<c>node_announcement_2</c>.</summary>
    V2 = 2
}