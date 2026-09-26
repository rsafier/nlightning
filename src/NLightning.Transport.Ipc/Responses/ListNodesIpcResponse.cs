using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for ListNodes (ClientCommand 17): the announced nodes of the gossip graph, ordered by node id.
/// </summary>
[MessagePackObject]
public sealed class ListNodesIpcResponse
{
    [Key(0)] public required List<GraphNodeIpcInfo> Nodes { get; init; }

    public static ListNodesIpcResponse FromClientResponse(ListNodesClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListNodesIpcResponse
        {
            Nodes = clientResponse.Nodes.Select(GraphNodeIpcInfo.From).ToList()
        };
    }
}

/// <summary>One node of a <see cref="ListNodesIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class GraphNodeIpcInfo
{
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>The alias as UTF-8 text, up to its first zero byte.</summary>
    [Key(1)] public required string Alias { get; init; }

    /// <summary>The color as <c>#rrggbb</c>.</summary>
    [Key(2)] public required string Color { get; init; }

    /// <summary>The announced addresses as <c>host:port</c> (IPv6 in brackets).</summary>
    [Key(3)] public required List<string> Addresses { get; init; }

    /// <summary>The announced feature bits, hex in wire order.</summary>
    [Key(4)] public required string Features { get; init; }

    /// <summary>The announcement's timestamp (UNIX seconds).</summary>
    [Key(5)] public uint Timestamp { get; init; }

    /// <summary>The graph channels the node is an end of.</summary>
    [Key(6)] public int ChannelCount { get; init; }

    /// <summary>The IPC form of a graph node (also used by <c>describegraph</c>'s page).</summary>
    public static GraphNodeIpcInfo From(GraphNodeInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new GraphNodeIpcInfo
        {
            NodeId = node.Node.NodeId,
            Alias = node.Node.AliasText,
            Color = node.Node.ColorHex,
            Addresses = node.Node.Addresses.Select(a => a.ToString()).ToList(),
            Features = Convert.ToHexStringLower(node.Node.Features.Span),
            Timestamp = node.Node.Timestamp,
            ChannelCount = node.ChannelCount
        };
    }
}