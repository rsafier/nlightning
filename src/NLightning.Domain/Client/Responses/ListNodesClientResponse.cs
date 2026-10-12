namespace NLightning.Domain.Client.Responses;

using Gossip.Graph;

/// <summary>
/// The announced nodes of the gossip graph, ordered by node id (<c>ClientCommand.ListNodes</c>).
/// </summary>
/// <param name="Nodes">Each node with the number of graph channels it is an end of.</param>
public sealed record ListNodesClientResponse(IReadOnlyList<GraphNodeInfo> Nodes);

/// <summary>A node announcement of the graph and the number of its graph channels.</summary>
public sealed record GraphNodeInfo(GraphNode Node, int ChannelCount);