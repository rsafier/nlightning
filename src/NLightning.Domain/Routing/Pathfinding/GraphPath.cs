namespace NLightning.Domain.Routing.Pathfinding;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Gossip.Graph;
using Models;

/// <summary>
/// One hop of a <see cref="GraphPath"/>: the node reached, the channel into it and what its HTLC carries.
/// </summary>
/// <param name="NodeId">The node this hop reaches (the last hop reaches the payee).</param>
/// <param name="ShortChannelId">The channel from the previous node (us for the first hop) to
/// <paramref name="NodeId"/>.</param>
/// <param name="AmountMsat">The HTLC amount <paramref name="NodeId"/> receives over that channel.</param>
/// <param name="CltvDelta">That HTLC's <c>cltv_expiry</c> minus the current height.</param>
/// <param name="FeeMsat">What <paramref name="NodeId"/> keeps for forwarding (0 for the payee).</param>
/// <param name="Policy">The policy of the previous node on that channel (whose htlc limits apply to this HTLC).
/// </param>
/// <param name="Probability">The estimated success probability of this edge.</param>
public sealed record PathHop(CompactPubKey NodeId, ShortChannelId ShortChannelId, ulong AmountMsat, uint CltvDelta,
                             ulong FeeMsat, GraphPolicy Policy, double Probability);

/// <summary>
/// A path found by <see cref="GraphPathfinder"/>, with exact per-hop amounts and CLTVs (BOLT 7 "HTLC Fees", BOLT 4
/// <c>outgoing_cltv_value</c>).
/// </summary>
/// <param name="Hops">The hops, first (our peer) to last (the payee).</param>
/// <param name="Cost">The pathfinder's cost of the path (lower is better).</param>
/// <param name="ShadowCltvOffset">The shadow offset included in every hop's CLTV.</param>
public sealed record GraphPath(IReadOnlyList<PathHop> Hops, double Cost, uint ShadowCltvOffset)
{
    /// <summary>What our first HTLC carries.</summary>
    public ulong AmountMsat => Hops[0].AmountMsat;

    /// <summary>What the payee receives.</summary>
    public ulong ReceiverAmountMsat => Hops[^1].AmountMsat;

    /// <summary>The fees of the whole path.</summary>
    public ulong FeeMsat => AmountMsat - ReceiverAmountMsat;

    /// <summary>Our first HTLC's <c>cltv_expiry</c> minus the current height.</summary>
    public uint TotalCltvDelta => Hops[0].CltvDelta;

    /// <summary>The product of the edge probabilities.</summary>
    public double Probability => Hops.Aggregate(1.0, (p, hop) => p * hop.Probability);

    /// <summary>The channel of our first HTLC.</summary>
    public ShortChannelId FirstChannel => Hops[0].ShortChannelId;

    /// <summary>
    /// The path after our first channel as BOLT 11 style routing entries (the shape <c>HintRouteBuilder</c> and
    /// <c>PaymentRoutePlanner</c> take): one per intermediate node, with the channel it forwards over and its policy
    /// on that channel. Empty when the payee is our peer.
    /// </summary>
    public IReadOnlyList<RoutingInfo> ToRoutingInfos()
    {
        var result = new List<RoutingInfo>(Hops.Count - 1);
        for (var i = 0; i < Hops.Count - 1; i++)
        {
            var next = Hops[i + 1];
            result.Add(new RoutingInfo(Hops[i].NodeId, next.ShortChannelId, next.Policy.FeeBaseMsat,
                                       next.Policy.FeeProportionalMillionths, next.Policy.CltvExpiryDelta));
        }

        return result;
    }
}