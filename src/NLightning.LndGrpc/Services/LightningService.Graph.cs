using System.Globalization;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Lnrpc;
using Mapping;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>GetChanInfo</c>: a channel of the gossip graph by <c>chan_id</c> or <c>chan_point</c>, else one of our own
    /// channels (LND keeps its private channels in its graph; ours holds the announced ones only), whose edge then has
    /// no policies. Unknown: <c>NOT_FOUND</c> "edge not found".
    /// </summary>
    public override Task<ChannelEdge> GetChanInfo(ChanInfoRequest request, ServerCallContext context)
    {
        var graph = Graph;
        var scid = request.ChanId != 0 ? new ShortChannelId(request.ChanId) : (ShortChannelId?)null;
        ChannelModel? own = null;
        if (scid is null)
        {
            if (string.IsNullOrEmpty(request.ChanPoint))
                throw InvalidArgument("chan_id or chan_point is required");

            var (txId, index) = ParseChannelPoint(request.ChanPoint);
            if (_graphStore is not null && _graphStore.TryGetChannelByFundingOutpoint(txId, index, out var found))
                scid = found;
            else
                own = _channels.FindChannels(c => ChannelPoint(c) == request.ChanPoint).FirstOrDefault();
        }

        if (scid is { } id && graph is not null && graph.TryGetChannel(id, out var edge))
            return Task.FromResult(ToEdge(edge));

        own ??= scid is { } wanted
                    ? _channels.FindChannels(c => c.ShortChannelId == wanted
                                               || (c.LocalAliases?.Contains(wanted) ?? false)).FirstOrDefault()
                    : null;
        return own is null
                   ? throw NotFound("edge not found")
                   : Task.FromResult(ToOwnEdge(own));
    }

    /// <summary>
    /// <c>GetNodeInfo</c>: a node of the gossip graph with its channel count and capacity (and channels with
    /// <c>include_channels</c>). Our own node answers from the node options when it has no announcement in the graph.
    /// </summary>
    public override Task<NodeInfo> GetNodeInfo(NodeInfoRequest request, ServerCallContext context)
    {
        CompactPubKey nodeId;
        try
        {
            nodeId = new CompactPubKey(Convert.FromHexString(request.PubKey));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw InvalidArgument("pub_key must be a 33-byte public key in hex");
        }

        var graph = Graph;
        GraphNode? node = null;
        var known = graph is not null && graph.TryGetNode(nodeId, out node);
        var ours = nodeId == _signer.GetNodePublicKey();
        if (!known && !ours && !(graph?.TryGetNodeIndex(nodeId, out _) ?? false))
            throw NotFound("unable to find node");

        var info = new NodeInfo
        {
            Node = node is not null
                       ? ToLightningNode(node)
                       : new LightningNode
                       {
                           PubKey = request.PubKey.ToLowerInvariant(),
                           Alias = ours ? _nodeOptions.Alias : string.Empty,
                           Color = ours ? "#" + _nodeOptions.Color.TrimStart('#').ToLowerInvariant() : "#000000"
                       }
        };
        if (graph is not null && graph.TryGetNodeIndex(nodeId, out var index))
        {
            var adjacency = graph.GetAdjacency(index);
            info.NumChannels = (uint)adjacency.Count;
            info.TotalCapacity = adjacency.Sum(a => (long)((a.Channel.EstimatedCapacityMsat ?? 0) / 1000));
            if (request.IncludeChannels)
                info.Channels.Add(adjacency.Select(a => ToEdge(a.Channel)));
        }

        return Task.FromResult(info);
    }

    /// <summary>
    /// <c>DescribeGraph</c>: every announced node and channel of the gossip graph (LND has no paging; the
    /// <c>LndGrpc:MaxDescribeGraphNodes</c>/<c>Edges</c> caps, off by default, keep the first by node id / SCID), plus
    /// our private channels with <c>include_unannounced</c>. No graph: <c>UNAVAILABLE</c>.
    /// </summary>
    public override Task<ChannelGraph> DescribeGraph(ChannelGraphRequest request, ServerCallContext context)
    {
        var graph = Graph ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                               "the gossip graph is disabled on this node"));
        var response = new ChannelGraph();
        IEnumerable<GraphNode> nodes = graph.Nodes.OrderBy(n => n.NodeId.ToString(), StringComparer.Ordinal);
        if (_options.MaxDescribeGraphNodes > 0)
            nodes = nodes.Take(_options.MaxDescribeGraphNodes);
        response.Nodes.Add(nodes.Select(ToLightningNode));

        IEnumerable<GraphChannel> edges = graph.Channels.Where(c => c.SpentAtHeight is null)
                                               .OrderBy(c => ToChanId(c.ShortChannelId));
        if (_options.MaxDescribeGraphEdges > 0)
            edges = edges.Take(_options.MaxDescribeGraphEdges);
        response.Edges.Add(edges.Select(ToEdge));

        if (request.IncludeUnannounced)
        {
            foreach (var channel in _channels.FindChannels(c => c.State == ChannelState.Open && !c.AnnounceChannel))
            {
                if (channel.ShortChannelId.BlockHeight == 0 || !graph.TryGetChannel(channel.ShortChannelId, out _))
                    response.Edges.Add(ToOwnEdge(channel));
            }
        }

        return Task.FromResult(response);
    }

    private ChannelEdge ToEdge(GraphChannel channel)
    {
        var edge = new ChannelEdge
        {
            ChannelId = ToChanId(channel.ShortChannelId),
            Node1Pub = channel.NodeId1.ToString(),
            Node2Pub = channel.NodeId2.ToString(),
            Capacity = (long)((channel.EstimatedCapacityMsat ?? 0) / 1000)
        };
        if (_graphStore is not null && _graphStore.TryGetFundingTxId(channel.ShortChannelId, out var fundingTxId))
            edge.ChanPoint = $"{fundingTxId}:{channel.ShortChannelId.OutputIndex}";

        var policy1 = channel.GetRoutingPolicy(0);
        var policy2 = channel.GetRoutingPolicy(1);
        if (policy1 is not null)
            edge.Node1Policy = ToRoutingPolicy(policy1);
        if (policy2 is not null)
            edge.Node2Policy = ToRoutingPolicy(policy2);
        edge.LastUpdate = Math.Max(policy1?.Timestamp ?? 0, policy2?.Timestamp ?? 0);
        return edge;
    }

    /// <summary>One of our channels as an edge: the BOLT 7 node order, no policies.</summary>
    private ChannelEdge ToOwnEdge(ChannelModel channel)
    {
        var ourId = _signer.GetNodePublicKey();
        var weAreFirst = GraphChannel.CompareNodeIds(ourId, channel.RemoteNodeId) < 0;
        return new ChannelEdge
        {
            ChannelId = ToChanId(channel.ShortChannelId),
            ChanPoint = ChannelPoint(channel),
            Node1Pub = (weAreFirst ? ourId : channel.RemoteNodeId).ToString(),
            Node2Pub = (weAreFirst ? channel.RemoteNodeId : ourId).ToString(),
            Capacity = channel.FundingOutput?.Amount.Satoshi ?? 0
        };
    }

    private static RoutingPolicy ToRoutingPolicy(GraphPolicy policy) => new()
    {
        TimeLockDelta = policy.CltvExpiryDelta,
        MinHtlc = (long)policy.HtlcMinimumMsat,
        FeeBaseMsat = policy.FeeBaseMsat,
        FeeRateMilliMsat = policy.FeeProportionalMillionths,
        Disabled = policy.IsDisabled,
        MaxHtlcMsat = policy.HtlcMaximumMsat,
        LastUpdate = policy.Timestamp,
        InboundFeeBaseMsat = unchecked((int)policy.InboundFeeBaseMsat),
        InboundFeeRateMilliMsat = unchecked((int)policy.InboundFeeProportionalMillionths)
    };

    private static LightningNode ToLightningNode(GraphNode node)
    {
        var item = new LightningNode
        {
            LastUpdate = node.Timestamp,
            PubKey = node.NodeId.ToString(),
            Alias = node.AliasText,
            Color = node.ColorHex
        };
        item.Addresses.Add(node.Addresses.Select(a => new NodeAddress { Network = "tcp", Addr = a.ToString() }));
        item.Features.Add(LndFeatures.ToMap(node.Features.Span).ToDictionary(p => p.Key, p => p.Value));
        return item;
    }

    /// <summary><c>txid:index</c> (txid as displayed) to the outpoint.</summary>
    private static (TxId TxId, uint Index) ParseChannelPoint(string channelPoint)
    {
        var colon = channelPoint.LastIndexOf(':');
        if (colon != 64
         || !uint.TryParse(channelPoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                           out var index))
            throw InvalidArgument("chan_point must be <funding txid>:<output index>");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(channelPoint.AsSpan(0, colon));
        }
        catch (FormatException)
        {
            throw InvalidArgument("chan_point must be <funding txid>:<output index>");
        }

        Array.Reverse(bytes);
        return (bytes, index);
    }
}