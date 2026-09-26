namespace NLightning.Domain.Routing.Pathfinding;

using Crypto.ValueObjects;
using Gossip.Graph;

/// <summary>
/// The BOLT 7 shadow route (B7-RT-01): a random CLTV offset for the payee, so intermediate nodes cannot tell their
/// distance to the payee from the CLTV they see.
/// </summary>
public static class ShadowCltv
{
    /// <summary>The default cap on the offset (one day of blocks).</summary>
    public const uint DefaultMaxOffset = 144;

    /// <summary>
    /// A limited random walk from <paramref name="target"/> (BOLT 7 "MAY start a limited random walk on the graph,
    /// starting from the intended recipient and summing the <c>cltv_expiry_delta</c>s"): up to
    /// <paramref name="maxWalkHops"/> steps over enabled edges, stopping at random, summing each walked node's
    /// outgoing delta, capped at <paramref name="maxOffset"/>. A payee without usable channels gets a uniform random
    /// offset in <c>[0, maxOffset]</c>. Deterministic for a seeded <paramref name="random"/>.
    /// </summary>
    public static uint ComputeOffset(IGraphView graph, CompactPubKey target, Random random,
                                     uint maxOffset = DefaultMaxOffset, int maxWalkHops = 3)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(random);
        if (!graph.TryGetNodeIndex(target, out var current))
            return (uint)random.Next(0, (int)Math.Min(maxOffset, int.MaxValue - 1) + 1);

        uint sum = 0;
        for (var step = 0; step < maxWalkHops; step++)
        {
            var edges = graph.GetAdjacency(current)
                             .Where(a => a.OutgoingPolicy is { IsDisabled: false } && a.Channel.SpentAtHeight is null)
                             .ToList();
            if (edges.Count == 0)
                break;

            var chosen = edges[random.Next(edges.Count)];
            sum += chosen.OutgoingPolicy!.CltvExpiryDelta;
            current = chosen.NeighborIndex;

            // Stop early half of the time after each step
            if (random.Next(2) == 0)
                break;
        }

        if (sum == 0)
            return (uint)random.Next(0, (int)Math.Min(maxOffset, int.MaxValue - 1) + 1);

        return Math.Min(sum, maxOffset);
    }
}