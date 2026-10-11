using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Gossip.Graph;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// A read-only, immutable snapshot of the network graph, used by pathfinding (plan BOLT7 §3.10). Every node that is
/// an end of a channel has a dense index in <c>[0, NodeCount)</c>, so pathfinding can keep its state in arrays.
/// </summary>
/// <remarks>
/// Implementations must not change after construction: readers take no lock. <see cref="GraphSnapshot"/> is the
/// Domain implementation.
/// </remarks>
public interface IGraphView
{
    /// <summary>The number of indexed nodes (every channel end, announced or not).</summary>
    int NodeCount { get; }

    /// <summary>The number of channels.</summary>
    int ChannelCount { get; }

    /// <summary>All channels.</summary>
    IEnumerable<GraphChannel> Channels { get; }

    /// <summary>All announced nodes.</summary>
    IEnumerable<GraphNode> Nodes { get; }

    /// <summary>The dense index of <paramref name="nodeId"/>.</summary>
    bool TryGetNodeIndex(CompactPubKey nodeId, out int index);

    /// <summary>The node id at <paramref name="index"/>.</summary>
    CompactPubKey GetNodeId(int index);

    /// <summary>The announcement of the node at <paramref name="index"/>, or null when it never announced itself.</summary>
    GraphNode? GetNode(int index);

    /// <summary>The announcement of <paramref name="nodeId"/>.</summary>
    bool TryGetNode(CompactPubKey nodeId, [NotNullWhen(true)] out GraphNode? node);

    /// <summary>The channel with <paramref name="shortChannelId"/>.</summary>
    bool TryGetChannel(ShortChannelId shortChannelId, [NotNullWhen(true)] out GraphChannel? channel);

    /// <summary>The channels of the node at <paramref name="index"/>, each with the index of its other end.</summary>
    IReadOnlyList<GraphAdjacency> GetAdjacency(int index);
}