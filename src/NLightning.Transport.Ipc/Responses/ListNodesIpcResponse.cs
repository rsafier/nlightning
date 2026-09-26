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
            Nodes = clientResponse.Nodes.Select(n => new GraphNodeIpcInfo
            {
                NodeId = n.Node.NodeId,
                Alias = n.Node.AliasText,
                Color = n.Node.ColorHex,
                Addresses = n.Node.Addresses.Select(a => a.ToString()).ToList(),
                Features = Convert.ToHexStringLower(n.Node.Features.Span),
                Timestamp = n.Node.Timestamp,
                ChannelCount = n.ChannelCount
            }).ToList()
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
}