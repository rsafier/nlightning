using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Node.Options;
using Interfaces;

/// <summary>
/// Lists the announced nodes of the gossip graph (ClientCommand 17, BOLT 7 plan G2-T6), ordered by node id, each with
/// the number of graph channels it is an end of. Refused with <c>invalid_operation</c> while the graph is disabled
/// (<c>Gossip:Enabled</c>; off on mainnet by default, plan D12).
/// </summary>
public sealed class ListNodesClientHandler : IClientCommandHandler<ListNodesClientRequest, ListNodesClientResponse>
{
    private readonly IGraphStore _graphStore;
    private readonly GossipGraphOptions _options;
    private readonly NodeOptions _nodeOptions;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListNodes;

    public ListNodesClientHandler(IGraphStore graphStore, IOptions<GossipGraphOptions> options,
                                  IOptions<NodeOptions> nodeOptions)
    {
        _graphStore = graphStore;
        _options = options.Value;
        _nodeOptions = nodeOptions.Value;
    }

    /// <inheritdoc/>
    public Task<ListNodesClientResponse> HandleAsync(ListNodesClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphClientGuards.ThrowIfDisabled(_options, _nodeOptions);

        var snapshot = _graphStore.GetSnapshot();
        var channelCounts = GraphClientGuards.CountChannels(snapshot);
        var nodes = snapshot.Nodes
                            .Where(n => request.NodeId is not { } only || n.NodeId == only)
                            .OrderBy(n => n.NodeId, GraphClientGuards.NodeIdComparer)
                            .Select(n => new GraphNodeInfo(n, channelCounts.GetValueOrDefault(n.NodeId)))
                            .ToList();
        return Task.FromResult(new ListNodesClientResponse(nodes));
    }
}

/// <summary>Shared checks of the graph listings.</summary>
internal static class GraphClientGuards
{
    /// <summary>Orders node ids as BOLT 7 does (lexicographically by their compressed bytes).</summary>
    public static readonly IComparer<CompactPubKey> NodeIdComparer =
        Comparer<CompactPubKey>.Create((a, b) => GraphChannel.CompareNodeIds(a, b));

    public static void ThrowIfDisabled(GossipGraphOptions options, NodeOptions nodeOptions)
    {
        if (!options.IsEnabledFor(nodeOptions.BitcoinNetwork))
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "The gossip graph is disabled on this node (Gossip:Enabled)");
    }

    public static Dictionary<CompactPubKey, int> CountChannels(IGraphView snapshot)
    {
        var counts = new Dictionary<CompactPubKey, int>();
        foreach (var channel in snapshot.Channels)
        {
            counts[channel.NodeId1] = counts.GetValueOrDefault(channel.NodeId1) + 1;
            counts[channel.NodeId2] = counts.GetValueOrDefault(channel.NodeId2) + 1;
        }

        return counts;
    }
}