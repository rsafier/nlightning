namespace NLightning.Application.Payments.Send;

using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Models;
using Domain.Money;
using Domain.Payments.Policies;
using Domain.Routing.Pathfinding;
using Routing;

/// <summary>
/// LND <c>QueryRoutes</c> over the payment planner (NL-1242, <see cref="Routing.Interfaces.IRouteQueryService.QueryRouteAsync"/>).
/// </summary>
public sealed partial class PaymentService
{
    /// <inheritdoc />
    public async Task<RouteQuote> QueryRouteAsync(RouteQueryRequest query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Amount.IsZero)
            throw new ArgumentException("The amount must be positive.", nameof(query));

        var ourNodeId = _secureKeyManager.GetNodePubKey();
        var circular = query.Payee == ourNodeId;
        if (query.LastHop is { } lastHopNode && lastHopNode == query.Payee)
            throw new ArgumentException("The last hop cannot be the destination.", nameof(query));
        if (circular && query.LastHop == ourNodeId)
            throw new ArgumentException("A circular route cannot come back from this node.", nameof(query));

        var height = _blockchainMonitor.LastProcessedBlockHeight;
        if (height == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        var allChannels = await GetUsableChannelsAsync(cancellationToken);
        var outgoing = query.OutgoingChannels is { } allowed
                           ? allChannels.Where(c => allowed.Contains(c.ShortChannelId)).ToList()
                           : allChannels;
        if (outgoing.Count == 0)
            throw new InvalidOperationException("No usable channel of ours is allowed for the first hop.");

        var graph = _graphPathSource?.CreateContext(0);
        if (graph is not null && !query.UseMissionControl)
            graph = graph with { Liquidity = new LiquidityEstimates(), PenalizedNodes = new HashSet<CompactPubKey>() };

        var constraints = new RouteConstraints();
        constraints.ExcludedNodes.UnionWith(query.IgnoredNodes.Where(n => n != ourNodeId && n != query.Payee));
        foreach (var (from, to) in query.IgnoredPairs)
            ExcludePair(constraints, from, to, ourNodeId, outgoing, graph?.Graph, query.RouteHints, query.Payee);

        var finalCltv = checked(height + query.FinalCltvDelta);
        uint? firstHopCap = query.MaxTotalCltvDelta is { } limit ? checked(height + limit) : null;
        var feeLimit = query.MaxFee.MilliSatoshi;

        // A circular route comes back over one of our channels whose peer's channel_update we hold
        IReadOnlyList<IncomingChannelCandidate>? incoming = null;
        if (circular)
        {
            incoming = BuildIncomingCandidates(allChannels, null)
                      .Where(c => query.LastHop is not { } last || c.PeerNodeId == last)
                      .Where(c => !query.IgnoredPairs.Any(p => p.From == c.PeerNodeId && p.To == ourNodeId))
                      .Where(c => !constraints.ExcludedNodes.Contains(c.PeerNodeId))
                      .ToList();
            if (incoming.Count == 0)
                throw new InvalidOperationException("No channel of ours can bring a circular route back.");
        }

        // A last hop on the way to someone else: plan to it, then append its edge to the destination
        LastEdge? lastEdge = null;
        var target = query.Payee;
        var targetAmount = query.Amount;
        var targetCltv = finalCltv;
        var hints = query.RouteHints;
        if (!circular && query.LastHop is { } lastHop)
        {
            if (lastHop == ourNodeId)
            {
                // Only our own channels to the destination: no graph, no hints
                graph = null;
                hints = [];
            }
            else
            {
                lastEdge = FindLastEdge(lastHop, query, constraints, graph?.Graph)
                        ?? throw new InvalidOperationException(
                               $"No usable channel from the last hop {lastHop} to the destination.");
                var lastFee = ForwardingFee.CalculateMsat(lastEdge.FeeBaseMsat, lastEdge.FeeProportionalMillionths,
                                                          query.Amount.MilliSatoshi);
                if (lastFee > feeLimit)
                    throw new InvalidOperationException("The last hop's fee alone exceeds the fee limit.");

                feeLimit -= lastFee;
                // The route to the last hop must not pass through the destination itself
                constraints.ExcludedNodes.Add(query.Payee);
                target = lastHop;
                targetAmount = LightningMoney.MilliSatoshis(checked(query.Amount.MilliSatoshi + lastFee));
                targetCltv = checked(finalCltv + lastEdge.CltvExpiryDelta);
                hints = [];
            }
        }

        var paymentTarget = new PaymentTarget(target, new Hash(new byte[32]), new Secret(new byte[32]), targetAmount,
                                              query.FinalCltvDelta, hints);
        var request = new PaymentPlanRequest(paymentTarget, targetAmount.MilliSatoshi, targetAmount.MilliSatoshi,
                                             feeLimit, 1, height, ourNodeId, outgoing.Select(ToCandidate).ToList(),
                                             CreateLiquidityProbe(allChannels, height), constraints,
                                             _sendOptions.Value.MinPartMsat, null, graph, incoming,
                                             AbsoluteFinalCltv: targetCltv, MaxFirstHopCltvExpiry: firstHopCap);
        if (!_planner.TryPlan(request, out var planned, out var reason))
            throw new InvalidOperationException(reason);

        var part = planned[0];
        var route = part.Route;
        if (lastEdge is not null)
        {
            // The planned route ends at the last hop with what it must receive; it forwards the amount on
            var hops = route.Hops.Take(route.Hops.Count - 1).ToList();
            hops.Add(new RouteHop(lastEdge.From, query.Amount, finalCltv, lastEdge.ShortChannelId));
            hops.Add(new RouteHop(query.Payee, query.Amount, finalCltv, null));
            route = new PaymentRoute(hops, route.FirstHopAmount, route.FirstHopCltvExpiry, route.PaymentHash,
                                     route.PaymentSecret);
        }

        if (firstHopCap is { } cap && route.FirstHopCltvExpiry > cap)
            throw new InvalidOperationException(
                $"The route's total time lock {route.FirstHopCltvExpiry} exceeds the limit {cap}.");

        return new RouteQuote(route, part.Channel, EstimateProbability(route, graph), height, part.Description);
    }

    /// <summary>The last forwarding edge of a route with a fixed last hop (LND's <c>last_hop_pubkey</c>).</summary>
    private sealed record LastEdge(CompactPubKey From, ShortChannelId ShortChannelId, uint FeeBaseMsat,
                                   uint FeeProportionalMillionths, ushort CltvExpiryDelta);

    /// <summary>
    /// The cheapest usable edge from <paramref name="lastHop"/> to the destination for the query's amount: its route
    /// hints' last entries from that node, then its graph channels to the destination (enabled policy, within their
    /// HTLC bounds, not ignored).
    /// </summary>
    private static LastEdge? FindLastEdge(CompactPubKey lastHop, RouteQueryRequest query, RouteConstraints constraints,
                                          IGraphView? graph)
    {
        var amount = query.Amount.MilliSatoshi;
        var candidates = new List<LastEdge>();
        foreach (var hint in query.RouteHints)
        {
            if (hint.Count > 0 && hint[^1] is { } entry && entry.CompactPubKey == lastHop
             && !constraints.ExcludedChannels.Contains(entry.ShortChannelId))
                candidates.Add(new LastEdge(lastHop, entry.ShortChannelId, entry.FeeBaseMsat,
                                            entry.FeeProportionalMillionths, entry.CltvExpiryDelta));
        }

        if (graph is not null && graph.TryGetNodeIndex(lastHop, out var index))
        {
            foreach (var adjacency in graph.GetAdjacency(index))
            {
                var channel = adjacency.Channel;
                if (channel.GetOtherNode(lastHop) != query.Payee
                 || constraints.ExcludedChannels.Contains(channel.ShortChannelId)
                 || constraints.ExcludedEdges.Contains(DirectedChannel.Between(channel.ShortChannelId, lastHop,
                                                                                query.Payee)))
                    continue;

                var policy = channel.GetRoutingPolicy(channel.GetDirectionFrom(lastHop));
                if (policy is null || policy.IsDisabled || amount < policy.HtlcMinimumMsat
                 || (policy.HtlcMaximumMsat != 0 && amount > policy.HtlcMaximumMsat))
                    continue;

                candidates.Add(new LastEdge(lastHop, channel.ShortChannelId, policy.FeeBaseMsat,
                                            policy.FeeProportionalMillionths, policy.CltvExpiryDelta));
            }
        }

        return candidates.OrderBy(e => ForwardingFee.CalculateMsat(e.FeeBaseMsat, e.FeeProportionalMillionths,
                                                                    amount))
                         .ThenBy(e => e.CltvExpiryDelta)
                         .FirstOrDefault();
    }

    /// <summary>
    /// Excludes the directed pair <paramref name="from"/> → <paramref name="to"/>: our channels to <c>to</c> when
    /// <c>from</c> is us, that direction of every graph channel between them, and the hint channels <c>from</c>
    /// forwards to <c>to</c> over.
    /// </summary>
    private static void ExcludePair(RouteConstraints constraints, CompactPubKey from, CompactPubKey to,
                                    CompactPubKey ourNodeId, List<ChannelModel> outgoing, IGraphView? graph,
                                    IReadOnlyList<IReadOnlyList<RoutingInfo>> hints, CompactPubKey payee)
    {
        if (from == ourNodeId)
        {
            foreach (var channel in outgoing.Where(c => c.RemoteNodeId == to))
                constraints.ExcludedLocalChannels.Add(channel.ChannelId);
        }

        if (graph is not null && graph.TryGetNodeIndex(from, out var index))
        {
            foreach (var adjacency in graph.GetAdjacency(index))
            {
                if (adjacency.Channel.GetOtherNode(from) == to)
                    constraints.ExcludedEdges.Add(DirectedChannel.Between(adjacency.Channel.ShortChannelId, from, to));
            }
        }

        foreach (var hint in hints)
        {
            for (var i = 0; i < hint.Count; i++)
            {
                var next = i + 1 < hint.Count ? hint[i + 1].CompactPubKey : payee;
                if (hint[i].CompactPubKey == from && next == to)
                    constraints.ExcludedChannels.Add(hint[i].ShortChannelId);
            }
        }
    }
}