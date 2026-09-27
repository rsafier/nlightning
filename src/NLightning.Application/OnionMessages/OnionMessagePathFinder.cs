using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.OnionMessages;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Graph;
using Domain.Node.Interfaces;
using Domain.Protocol.OnionMessages;
using Gossip.Graph.Interfaces;

/// <summary>
/// Where onion messages can go (plan OM2-T3/T4): the connected peers that negotiated <c>option_onion_messages</c>, the
/// node behind a <c>short_channel_id</c> or a <c>sciddir_or_pubkey</c>, and the unblinded hops to reach a node.
/// </summary>
/// <remarks>
/// <para>We only send to connected peers and never connect to forward or reply (plan D6).</para>
/// <para>A <c>short_channel_id</c> resolves through our channels (the real SCID or one of our local aliases, BOLT 4
/// reader) and then through the graph (an announced channel with us at one end, for forwarding; any announced channel
/// for a <c>sciddir_or_pubkey</c>, BOLT 1: direction 0 is <c>node_id_1</c>, the lesser id).</para>
/// <para>A path to a node that is not a peer is a breadth-first search over the graph from our connected onion-message
/// peers, through announced nodes that advertise bit 38/39, of at most <see cref="OnionMessageOptions.MaxPathHops"/>
/// unblinded hops; the destination must advertise the bit too.</para>
/// </remarks>
public sealed class OnionMessagePathFinder
{
    private const int OnionMessagesOptionalBit = 39;

    private readonly IPeerManager _peerManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IGraphStore? _graphStore;
    private readonly CompactPubKey _ourNodeId;
    private readonly int _maxPathHops;

    public OnionMessagePathFinder(IPeerManager peerManager, IChannelMemoryRepository channelMemoryRepository,
                                  CompactPubKey ourNodeId, int maxPathHops, IGraphStore? graphStore = null)
    {
        _peerManager = peerManager;
        _channelMemoryRepository = channelMemoryRepository;
        _ourNodeId = ourNodeId;
        _maxPathHops = maxPathHops;
        _graphStore = graphStore;
    }

    /// <summary>
    /// The connection of <paramref name="nodeId"/> when it is connected and negotiated <c>option_onion_messages</c>.
    /// </summary>
    public IPeerService? GetOnionMessagePeer(CompactPubKey nodeId)
    {
        var peer = _peerManager.GetPeer(nodeId);
        if (peer is null || !peer.TryGetPeerService(out var service))
            return null;

        return service.Features.OptionOnionMessages == FeatureSupport.No ? null : service;
    }

    /// <summary>
    /// The connected peers that negotiated <c>option_onion_messages</c>, those we have an open channel with first.
    /// </summary>
    public IReadOnlyList<CompactPubKey> ListOnionMessagePeers()
    {
        return _peerManager.ListPeers()
                           .Select(p => p.NodeId)
                           .Where(id => GetOnionMessagePeer(id) is not null)
                           .OrderByDescending(HasOpenChannelWith)
                           .ToList();
    }

    /// <summary>
    /// Whether we have an open channel with <paramref name="nodeId"/>.
    /// </summary>
    public bool HasOpenChannelWith(CompactPubKey nodeId) =>
        _channelMemoryRepository.FindChannels(c => c.RemoteNodeId == nodeId && c.State == ChannelState.Open).Count > 0;

    /// <summary>
    /// The peer at the other end of our channel <paramref name="shortChannelId"/> (real SCID or local alias), or null.
    /// </summary>
    public CompactPubKey? ResolveOurChannel(ShortChannelId shortChannelId)
    {
        var channel = _channelMemoryRepository
                     .FindChannels(c => c.ShortChannelId == shortChannelId
                                     || (c.LocalAliases?.Contains(shortChannelId) ?? false))
                     .FirstOrDefault();
        if (channel is not null)
            return channel.RemoteNodeId;

        if (TryGetGraphChannel(shortChannelId, out var graphChannel))
        {
            if (graphChannel.NodeId1 == _ourNodeId)
                return graphChannel.NodeId2;
            if (graphChannel.NodeId2 == _ourNodeId)
                return graphChannel.NodeId1;
        }

        return null;
    }

    /// <summary>
    /// The node a <c>sciddir_or_pubkey</c> names, or null when its channel is unknown.
    /// </summary>
    public CompactPubKey? Resolve(SciddirOrPubkey node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.NodeId is { } nodeId)
            return nodeId;

        var shortChannelId = node.ShortChannelId!.Value;
        if (TryGetGraphChannel(shortChannelId, out var graphChannel))
            return node.Direction == 0 ? graphChannel.NodeId1 : graphChannel.NodeId2;

        if (ResolveOurChannel(shortChannelId) is not { } remote)
            return null;

        // Our unannounced channel: its ends are us and the peer, in BOLT 7 order (the lesser id is node_id_1)
        var ourIsFirst = GraphChannel.CompareNodeIds(_ourNodeId, remote) < 0;
        return (node.Direction == 0) == ourIsFirst ? _ourNodeId : remote;
    }

    /// <summary>
    /// The unblinded hops to reach <paramref name="target"/>: empty when it is a connected onion-message peer,
    /// otherwise the node ids from our peer up to (not including) <paramref name="target"/>; null when there is no
    /// path.
    /// </summary>
    public IReadOnlyList<CompactPubKey>? FindPrefix(CompactPubKey target)
    {
        if (target == _ourNodeId)
            return null;
        if (GetOnionMessagePeer(target) is not null)
            return [];
        if (_maxPathHops == 0 || _graphStore is not { IsLoaded: true })
            return null;

        var graph = _graphStore.GetSnapshot();
        if (!graph.TryGetNodeIndex(target, out var targetIndex) || !AdvertisesOnionMessages(graph, targetIndex))
            return null;

        // Breadth-first from every connected onion-message peer; parents give the path back
        var parents = new Dictionary<int, int>();
        var queue = new Queue<(int Index, int Depth)>();
        foreach (var peer in ListOnionMessagePeers())
        {
            if (graph.TryGetNodeIndex(peer, out var index) && parents.TryAdd(index, -1))
                queue.Enqueue((index, 1));
        }

        graph.TryGetNodeIndex(_ourNodeId, out var ourIndex);
        while (queue.Count > 0)
        {
            var (index, depth) = queue.Dequeue();
            foreach (var adjacency in graph.GetAdjacency(index))
            {
                if (adjacency.Channel.SpentAtHeight is not null)
                    continue;

                var neighbor = adjacency.NeighborIndex;
                if (neighbor == targetIndex)
                    return BuildPath(graph, parents, index);
                if (depth >= _maxPathHops || neighbor == ourIndex || parents.ContainsKey(neighbor)
                 || !AdvertisesOnionMessages(graph, neighbor))
                    continue;

                parents[neighbor] = index;
                queue.Enqueue((neighbor, depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// Whether big-endian wire feature bits set bit 38 or 39 (<c>option_onion_messages</c>).
    /// </summary>
    public static bool AdvertisesOnionMessages(ReadOnlySpan<byte> wireFeatures)
    {
        const int byteFromEnd = OnionMessagesOptionalBit / 8;
        if (wireFeatures.Length <= byteFromEnd)
            return false;
        var value = wireFeatures[wireFeatures.Length - 1 - byteFromEnd];
        // Bits 38 and 39 are bits 6 and 7 of that byte
        return (value & 0b1100_0000) != 0;
    }

    private static bool AdvertisesOnionMessages(IGraphView graph, int index) =>
        graph.GetNode(index) is { } node && AdvertisesOnionMessages(node.Features.Span);

    private static List<CompactPubKey> BuildPath(IGraphView graph, Dictionary<int, int> parents, int last)
    {
        var path = new List<CompactPubKey>();
        for (var index = last; index != -1; index = parents[index])
            path.Add(graph.GetNodeId(index));
        path.Reverse();
        return path;
    }

    private bool TryGetGraphChannel(ShortChannelId shortChannelId, [NotNullWhen(true)] out GraphChannel? channel)
    {
        channel = null;
        return _graphStore is { IsLoaded: true } store
            && store.GetSnapshot().TryGetChannel(shortChannelId, out channel);
    }
}