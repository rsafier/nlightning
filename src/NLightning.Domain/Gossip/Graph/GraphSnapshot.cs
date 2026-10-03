using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Gossip.Graph;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The immutable Domain <see cref="IGraphView"/>: indexes the nodes densely and keeps per-node adjacency lists.
/// Build one per graph version (the graph store rebuilds it at most every <c>Gossip:SnapshotInterval</c>).
/// </summary>
public sealed class GraphSnapshot : IGraphView
{
    private readonly Dictionary<CompactPubKey, int> _indexByNode = new();
    private readonly List<CompactPubKey> _nodeIds = [];
    private readonly List<List<GraphAdjacency>> _adjacency = [];
    private readonly List<GraphNode?> _nodes = [];
    private readonly Dictionary<ShortChannelId, GraphChannel> _channels = new();

    /// <summary>
    /// An empty graph.
    /// </summary>
    public static GraphSnapshot Empty { get; } = new([], []);

    /// <summary>
    /// Builds the snapshot. A node announcement whose node has no channel is still indexed (it may be a payee).
    /// </summary>
    /// <exception cref="ArgumentException">Two channels share a short channel id, or two nodes share a node id.
    /// </exception>
    public GraphSnapshot(IEnumerable<GraphChannel> channels, IEnumerable<GraphNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(nodes);

        foreach (var channel in channels)
        {
            if (!_channels.TryAdd(channel.ShortChannelId, channel))
                throw new ArgumentException($"Duplicate channel {channel.ShortChannelId}.", nameof(channels));

            var index1 = GetOrAddIndex(channel.NodeId1);
            var index2 = GetOrAddIndex(channel.NodeId2);
            _adjacency[index1].Add(new GraphAdjacency(channel, index2, 0));
            _adjacency[index2].Add(new GraphAdjacency(channel, index1, 1));
        }

        foreach (var node in nodes)
        {
            var index = GetOrAddIndex(node.NodeId);
            if (_nodes[index] is not null)
                throw new ArgumentException($"Duplicate node {node.NodeId}.", nameof(nodes));

            _nodes[index] = node;
        }
    }

    /// <inheritdoc />
    public int NodeCount => _nodeIds.Count;

    /// <inheritdoc />
    public int ChannelCount => _channels.Count;

    /// <inheritdoc />
    public IEnumerable<GraphChannel> Channels => _channels.Values;

    /// <inheritdoc />
    public IEnumerable<GraphNode> Nodes => _nodes.OfType<GraphNode>();

    /// <inheritdoc />
    public bool TryGetNodeIndex(CompactPubKey nodeId, out int index) => _indexByNode.TryGetValue(nodeId, out index);

    /// <inheritdoc />
    public CompactPubKey GetNodeId(int index) => _nodeIds[index];

    /// <inheritdoc />
    public GraphNode? GetNode(int index) => _nodes[index];

    /// <inheritdoc />
    public bool TryGetNode(CompactPubKey nodeId, [NotNullWhen(true)] out GraphNode? node)
    {
        node = _indexByNode.TryGetValue(nodeId, out var index) ? _nodes[index] : null;
        return node is not null;
    }

    /// <inheritdoc />
    public bool TryGetChannel(ShortChannelId shortChannelId, [NotNullWhen(true)] out GraphChannel? channel) =>
        _channels.TryGetValue(shortChannelId, out channel);

    /// <inheritdoc />
    public IReadOnlyList<GraphAdjacency> GetAdjacency(int index) => _adjacency[index];

    private int GetOrAddIndex(CompactPubKey nodeId)
    {
        if (_indexByNode.TryGetValue(nodeId, out var index))
            return index;

        index = _nodeIds.Count;
        _indexByNode.Add(nodeId, index);
        _nodeIds.Add(nodeId);
        _adjacency.Add([]);
        _nodes.Add(null);
        return index;
    }
}