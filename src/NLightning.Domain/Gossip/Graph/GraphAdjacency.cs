namespace NLightning.Domain.Gossip.Graph;

/// <summary>
/// One channel as seen from one of its ends.
/// </summary>
/// <param name="Channel">The channel.</param>
/// <param name="NeighborIndex">The graph index of the other end.</param>
/// <param name="LocalDirection">The direction whose origin is this end (0 when this end is <c>node_id_1</c>);
/// the other end's direction is <c>1 - LocalDirection</c>.</param>
public readonly record struct GraphAdjacency(GraphChannel Channel, int NeighborIndex, byte LocalDirection)
{
    /// <summary>
    /// The routing policy of the edge from this end to the neighbor (the <c>channel_update_2</c> when there is one,
    /// NL-878: <see cref="GraphChannel.GetRoutingPolicy"/>).
    /// </summary>
    public GraphPolicy? OutgoingPolicy => Channel.GetRoutingPolicy(LocalDirection);

    /// <summary>The routing policy of the edge from the neighbor to this end.</summary>
    public GraphPolicy? IncomingPolicy => Channel.GetRoutingPolicy((byte)(1 - LocalDirection));
}