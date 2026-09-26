using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Routing.Pathfinding;

/// <summary>
/// Plans the HTLCs of one payment round (NL-270): one route when one can carry the amount, else, when the invoice
/// offers <c>basic_mpp</c>, several parts over our direct channels to the payee and the invoice's route hints
/// (BOLT 4 "Basic Multi-Part Payments"). Pure: it reads only its request.
/// </summary>
/// <remarks>
/// <para>Paths: every usable channel of ours to the payee (direct), then every usable channel of ours to the first
/// node of each route hint followed by the hint's entries (<see cref="HintRouteBuilder.GetCandidates"/>), with the
/// hops' policies replaced by verified <c>channel_update</c>s (<see cref="RouteConstraints.PolicyOverrides"/>), and
/// without excluded nodes and channels. Each path's amounts and CLTVs are those of <see cref="HintRouteBuilder"/>
/// (final <c>outgoing_cltv_value</c> = height + <c>c</c> + 3 + <see cref="RouteConstraints.ExtraCltvDelta"/>); a path
/// whose total CLTV exceeds <see cref="RoutingOptions.MaxCltvExpiryDistance"/> is skipped.</para>
/// <para>An amount fits a path when the fee is within the remaining fee limit, our HTLC is at most what the engine
/// lets us send on that channel (<see cref="PaymentPlanRequest.MaxSendableMsat"/>, given the parts already planned on
/// it) and below its learnt bound, and every hint channel forwards at least its <c>htlc_minimum_msat</c>, at most its
/// <c>htlc_maximum_msat</c> and, with the parts already planned over it and the payment's parts still in flight over it
/// (<see cref="PaymentPlanRequest.HintForwardsInFlightMsat"/>), less than its learnt liquidity bound.</para>
/// <para>One part: the first path, in order (direct paths first, then hints in invoice order; each by what its channel
/// can send, largest first), that fits the whole amount. Split (only with <see cref="PaymentTarget.SupportsMpp"/> and
/// at least two parts allowed): paths by fee for the whole amount, cheapest first, then by what they can send; each
/// takes the largest amount that fits (at least <see cref="PaymentPlanRequest.MinPartMsat"/> unless that is all that
/// is left), until the amount is covered, within <see cref="PaymentPlanRequest.MaxParts"/>. Every part carries
/// <c>total_msat</c> = <see cref="PaymentPlanRequest.TotalMsat"/>.</para>
/// <para>Graph paths (BOLT 7 plan G4-T3, decision D7; only with <see cref="PaymentPlanRequest.Graph"/>): when no direct
/// or hint path carries the whole amount, <see cref="GraphPathfinder"/> searches the gossip graph from us to the payee
/// (Dijkstra backward from the payee, probability-weighted with <see cref="MissionControl"/>'s estimates) with our
/// usable channels as the first hops (their live sendable amount, never their gossip state), the invoice's route hints
/// as extra edges (so a private payee is reached through the graph and then its hint), the payment's exclusions, the
/// nodes mission control penalizes and the verified <c>channel_update</c>s of earlier failures
/// (<see cref="RouteConstraints.GraphPolicyOverrides"/>, this payment only), the fee limit and
/// <see cref="RoutingOptions.MaxCltvExpiryDistance"/>. It returns up to <see cref="GraphRoutingContext.PathsPerAmount"/>
/// diverse paths, tried after the direct and hint paths; for a split it is asked again for half the amount, a quarter,
/// and so on down to <see cref="PaymentPlanRequest.MinPartMsat"/>, so smaller channels are found too. A graph path's
/// hops also keep their <c>htlc_minimum_msat</c>, <c>htlc_maximum_msat</c> and capacity, which every part (and the
/// parts together, for the capacity) must respect, and its payee CLTV carries the shadow offset
/// (<see cref="GraphRoutingContext.ShadowCltvOffset"/>, cut to the CLTV limit).</para>
/// </remarks>
public sealed class PaymentRoutePlanner
{
    private static readonly IReadOnlyDictionary<ShortChannelId, ulong> s_noHintAssigned =
        new Dictionary<ShortChannelId, ulong>();

    /// <summary>How many times the graph is asked again for a smaller amount when splitting (half each time).</summary>
    private const int MaxGraphSplitLevels = 4;

    /// <summary>
    /// The policy of an extra first-hop edge from us: we charge ourselves nothing, add no CLTV, and the live state
    /// (<see cref="PathfindingRequest.LocalChannels"/>) bounds the amount.
    /// </summary>
    private static readonly GraphPolicy s_ownFirstHopPolicy = new(0, 0, 0, 0, 0, ulong.MaxValue, 0, 0);

    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly GraphPathfinder _pathfinder = new();

    public PaymentRoutePlanner(IOptions<NodeOptions> nodeOptions)
    {
        _nodeOptions = nodeOptions;
    }

    /// <summary>
    /// Plans the parts that deliver <see cref="PaymentPlanRequest.AmountMsat"/>.
    /// </summary>
    /// <param name="request">What to plan.</param>
    /// <param name="parts">The parts, when the amount can be covered.</param>
    /// <param name="failureReason">Why it cannot be.</param>
    /// <exception cref="ArgumentException">A zero amount, a payee that is us, or fewer than one part allowed.</exception>
    public bool TryPlan(PaymentPlanRequest request, [NotNullWhen(true)] out IReadOnlyList<PlannedPart>? parts,
                        [NotNullWhen(false)] out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AmountMsat == 0)
            throw new ArgumentException("The amount to plan must be positive.", nameof(request));
        if (request.Target.PayeeNodeId == request.OurNodeId)
            throw new ArgumentException("Cannot pay ourselves.", nameof(request));
        if (request.MaxParts < 1)
            throw new ArgumentException("At least one part must be allowed.", nameof(request));

        var inFlight = request.HintForwardsInFlightMsat ?? s_noHintAssigned;
        var reasons = new List<string>();
        var paths = BuildPaths(request, reasons);

        // One part, when one path can carry the whole amount: the direct and hint paths first, then the graph's
        var singleReasons = new List<string>();
        if (TryFitSingle(request, paths, inFlight, singleReasons, out parts))
        {
            failureReason = null;
            return true;
        }

        var seen = new HashSet<string>(paths.Select(Signature));
        var graphPaths = BuildGraphPaths(request, request.AmountMsat, seen);
        if (TryFitSingle(request, graphPaths, inFlight, singleReasons, out parts))
        {
            failureReason = null;
            return true;
        }

        if (request.Graph is not null && graphPaths.Count == 0)
            reasons.Add("the graph has no path for the whole amount within the limits");

        if (request.Target.SupportsMpp && request.MaxParts >= 2 && request.Graph is not null)
        {
            // Smaller amounts find the smaller channels a split can combine
            var amount = request.AmountMsat;
            for (var level = 0; level < MaxGraphSplitLevels; level++)
            {
                amount /= 2;
                if (amount == 0 || amount < request.MinPartMsat)
                    break;

                graphPaths.AddRange(BuildGraphPaths(request, amount, seen));
            }
        }

        paths.AddRange(graphPaths);
        if (paths.Count == 0)
        {
            parts = null;
            failureReason = reasons.Count == 0
                                ? "No route to the payee: it is not our peer and the invoice has no usable route hints."
                                : "No route to the payee: " + string.Join("; ", reasons) + ".";
            return false;
        }

        reasons.AddRange(singleReasons);
        if (!request.Target.SupportsMpp || request.MaxParts < 2)
        {
            parts = null;
            var splitNote = request.Target.SupportsMpp
                                ? " (splitting is turned off for this payment)"
                                : " (the invoice does not offer basic_mpp, so the payment cannot be split)";
            failureReason = "No usable route to the payee: " + string.Join("; ", reasons) + "." + splitNote;
            return false;
        }

        if (TrySplit(request, paths, inFlight, out parts, out var splitReason))
        {
            failureReason = null;
            return true;
        }

        failureReason = "No usable route to the payee: " + string.Join("; ", reasons) + "; " + splitReason + ".";
        return false;
    }

    private bool TryFitSingle(PaymentPlanRequest request, IEnumerable<CandidatePath> paths,
                              IReadOnlyDictionary<ShortChannelId, ulong> inFlight, List<string> reasons,
                              [NotNullWhen(true)] out IReadOnlyList<PlannedPart>? parts)
    {
        foreach (var path in paths)
        {
            if (TryFit(request, path, request.AmountMsat, request.MaxFeeMsat, [], inFlight, out var route,
                       out var reason))
            {
                parts = [new PlannedPart(path.Channel, route, path.Description)];
                return true;
            }

            reasons.Add($"{path.Description}: {reason}");
        }

        parts = null;
        return false;
    }

    private bool TrySplit(PaymentPlanRequest request, List<CandidatePath> paths,
                          IReadOnlyDictionary<ShortChannelId, ulong> inFlight,
                          [NotNullWhen(true)] out IReadOnlyList<PlannedPart>? parts, out string failureReason)
    {
        var finalCltv = FinalCltv(request);
        var total = LightningMoney.MilliSatoshis(request.TotalMsat);

        // Cheapest first (fee for the whole amount), then the largest channel
        var ordered = paths
                     .Select(p => (Path: p,
                                   Fee: HintRouteBuilder.BuildAlong(p.Hops, request.Target,
                                                                    LightningMoney.MilliSatoshis(request.AmountMsat),
                                                                    finalCltv + p.ShadowCltv, total)
                                                      .Fee.MilliSatoshi))
                     .OrderBy(p => p.Fee)
                     .ThenByDescending(p => p.Path.Sendable)
                     .Select(p => p.Path)
                     .ToList();

        var planned = new List<PlannedPart>();
        var localAssigned = new Dictionary<ChannelId, List<ulong>>();
        // A hint channel's liquidity bound covers every HTLC of the payment over it, the parts in flight included
        var hintAssigned = new Dictionary<ShortChannelId, ulong>(inFlight);
        var remaining = request.AmountMsat;
        var feeLeft = request.MaxFeeMsat;
        foreach (var path in ordered)
        {
            if (planned.Count == request.MaxParts)
                break;

            if (!localAssigned.TryGetValue(path.Channel.ChannelId, out var onChannel))
                localAssigned[path.Channel.ChannelId] = onChannel = [];

            var largest = LargestFit(request, path, remaining, feeLeft, onChannel, hintAssigned);
            if (largest is null)
                continue;

            var (amount, route) = largest.Value;
            if (amount < remaining && amount < request.MinPartMsat)
                continue;

            planned.Add(new PlannedPart(path.Channel, route, path.Description));
            onChannel.Add(route.FirstHopAmount.MilliSatoshi);
            for (var i = 0; i < path.Hops.Count; i++)
            {
                var scid = path.Hops[i].ShortChannelId;
                hintAssigned[scid] = hintAssigned.GetValueOrDefault(scid) + route.Hops[i].AmountToForward.MilliSatoshi;
            }

            remaining -= amount;
            feeLeft -= route.Fee.MilliSatoshi;
            if (remaining == 0)
            {
                parts = planned;
                failureReason = string.Empty;
                return true;
            }
        }

        parts = null;
        failureReason = planned.Count == request.MaxParts
                            ? $"split into {request.MaxParts} part(s), {remaining} msat would still be missing"
                            : $"even split over every path, {remaining} msat of {request.AmountMsat} msat would be "
                            + "missing";
        return false;
    }

    /// <summary>
    /// The largest amount up to <paramref name="upTo"/> that fits the path (binary search: fee and amounts grow with
    /// the amount), with its route; null when not even the smallest fits.
    /// </summary>
    private (ulong Amount, PaymentRoute Route)? LargestFit(PaymentPlanRequest request, CandidatePath path, ulong upTo,
                                                          ulong feeLeft, IReadOnlyList<ulong> onChannel,
                                                          IReadOnlyDictionary<ShortChannelId, ulong> hintAssigned)
    {
        if (TryFit(request, path, upTo, feeLeft, onChannel, hintAssigned, out var full, out _))
            return (upTo, full);

        ulong low = 1, high = upTo - 1;
        (ulong, PaymentRoute)? best = null;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (TryFit(request, path, mid, feeLeft, onChannel, hintAssigned, out var route, out _, ignoreMinimums: true))
            {
                best = (mid, route);
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        // The minimums bound from below: the largest amount must still meet them
        if (best is { } found
         && TryFit(request, path, found.Item1, feeLeft, onChannel, hintAssigned, out var checkedRoute, out _))
            return (found.Item1, checkedRoute);

        return null;
    }

    private bool TryFit(PaymentPlanRequest request, CandidatePath path, ulong amountMsat, ulong feeLeft,
                        IReadOnlyList<ulong> onChannel, IReadOnlyDictionary<ShortChannelId, ulong> hintAssigned,
                        [NotNullWhen(true)] out PaymentRoute? route, out string reason, bool ignoreMinimums = false)
    {
        route = HintRouteBuilder.BuildAlong(path.Hops, request.Target, LightningMoney.MilliSatoshis(amountMsat),
                                            FinalCltv(request) + path.ShadowCltv,
                                            LightningMoney.MilliSatoshis(request.TotalMsat));
        var fee = route.Fee.MilliSatoshi;
        if (fee > feeLeft)
        {
            reason = $"fee {fee} msat exceeds the limit of {feeLeft} msat";
            route = null;
            return false;
        }

        var firstHop = route.FirstHopAmount.MilliSatoshi;
        var sendable = onChannel.Count == 0
                           ? path.Sendable
                           : request.MaxSendableMsat(path.Channel.ChannelId, onChannel);
        if (request.Constraints.LocalLiquidityBoundsMsat.TryGetValue(path.Channel.ChannelId, out var localBound)
         && localBound <= sendable)
            sendable = localBound == 0 ? 0 : localBound - 1;
        if (firstHop > sendable)
        {
            reason = $"our channel {path.Channel.ShortChannelId} can send at most {sendable} msat, not {firstHop} msat";
            route = null;
            return false;
        }

        for (var i = 0; i < path.Hops.Count; i++)
        {
            var scid = path.Hops[i].ShortChannelId;
            var forwarded = route.Hops[i].AmountToForward.MilliSatoshi;
            if (request.Constraints.PolicyOverrides.TryGetValue(scid, out var policy))
            {
                if (!ignoreMinimums && forwarded < policy.HtlcMinimumMsat)
                {
                    reason = $"channel {scid} forwards at least {policy.HtlcMinimumMsat} msat";
                    route = null;
                    return false;
                }

                if (forwarded > policy.HtlcMaximumMsat)
                {
                    reason = $"channel {scid} forwards at most {policy.HtlcMaximumMsat} msat";
                    route = null;
                    return false;
                }
            }

            if (path.Limits is { } limits)
            {
                var limit = limits[i];
                if (!ignoreMinimums && forwarded < limit.HtlcMinimumMsat)
                {
                    reason = $"channel {scid} forwards at least {limit.HtlcMinimumMsat} msat";
                    route = null;
                    return false;
                }

                if (forwarded > limit.HtlcMaximumMsat)
                {
                    reason = $"channel {scid} forwards at most {limit.HtlcMaximumMsat} msat";
                    route = null;
                    return false;
                }

                if (limit.CapacityMsat is { } capacity && hintAssigned.GetValueOrDefault(scid) + forwarded > capacity)
                {
                    reason = $"channel {scid} holds at most {capacity} msat";
                    route = null;
                    return false;
                }
            }

            if (request.Constraints.ChannelLiquidityBoundsMsat.TryGetValue(scid, out var bound)
             && hintAssigned.GetValueOrDefault(scid) + forwarded >= bound)
            {
                reason = $"channel {scid} could not forward {bound} msat";
                route = null;
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// What <paramref name="routes"/> forward over each route-hint channel, together (for
    /// <see cref="PaymentPlanRequest.HintForwardsInFlightMsat"/>).
    /// </summary>
    public static Dictionary<ShortChannelId, ulong> SumHintForwards(IEnumerable<PaymentRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var sums = new Dictionary<ShortChannelId, ulong>();
        foreach (var route in routes)
        {
            foreach (var hop in route.Hops)
            {
                if (hop.OutgoingShortChannelId is { } scid)
                    sums[scid] = checked(sums.GetValueOrDefault(scid) + hop.AmountToForward.MilliSatoshi);
            }
        }

        return sums;
    }

    private uint FinalCltv(PaymentPlanRequest request) =>
        checked(request.Height + request.Target.MinFinalCltvExpiryDelta + HintRouteBuilder.FinalCltvSafetyOffset
              + request.Constraints.ExtraCltvDelta);

    private List<CandidatePath> BuildPaths(PaymentPlanRequest request, List<string> reasons)
    {
        var constraints = request.Constraints;
        var maxCltvDistance = _nodeOptions.Value.Routing.MaxCltvExpiryDistance;
        var finalCltv = FinalCltv(request);
        var direct = new List<CandidatePath>();
        var hinted = new List<CandidatePath>();

        foreach (var (description, hintIndex, rawPath) in HintRouteBuilder.GetCandidates(request.Target,
                                                                                          request.OurNodeId))
        {
            var path = rawPath.Select(entry => WithOverride(entry, constraints)).ToList();
            var firstNode = path.Count > 0 ? path[0].CompactPubKey : request.Target.PayeeNodeId;

            if (path.FirstOrDefault(e => constraints.ExcludedNodes.Contains(e.CompactPubKey)) is { } excludedNode)
            {
                reasons.Add($"{description}: node {excludedNode.CompactPubKey} failed earlier");
                continue;
            }

            if (path.FirstOrDefault(e => constraints.ExcludedChannels.Contains(e.ShortChannelId)) is { } excluded)
            {
                reasons.Add($"{description}: channel {excluded.ShortChannelId} failed earlier");
                continue;
            }

            var channels = request.Channels.Where(c => c.PeerNodeId == firstNode).ToList();
            if (channels.Count == 0)
            {
                // The direct candidate is only a candidate when the payee is our peer
                if (hintIndex >= 0 || request.Target.RouteHints.Count == 0)
                    reasons.Add($"{description}: no usable channel to {firstNode}");
                continue;
            }

            // The CLTV does not depend on the amount: check it once per path
            var probe = HintRouteBuilder.BuildAlong(path, request.Target, LightningMoney.MilliSatoshis(1), finalCltv);
            var totalCltvDelta = (ulong)probe.FirstHopCltvExpiry - request.Height;
            if (totalCltvDelta > maxCltvDistance)
            {
                reasons.Add($"{description}: total CLTV delta {totalCltvDelta} exceeds {maxCltvDistance} blocks");
                continue;
            }

            var usable = 0;
            foreach (var channel in channels)
            {
                if (constraints.ExcludedLocalChannels.Contains(channel.ChannelId))
                    continue;

                usable++;
                var sendable = request.MaxSendableMsat(channel.ChannelId, []);
                var candidate = new CandidatePath(channel, path, $"{description} over {channel.ShortChannelId}",
                                                  hintIndex, sendable);
                (hintIndex < 0 ? direct : hinted).Add(candidate);
            }

            if (usable == 0)
                reasons.Add($"{description}: our channels to {firstNode} failed earlier");
        }

        // Direct paths first, then the hints in invoice order; the largest channel first within each
        return direct.OrderByDescending(p => p.Sendable)
                     .Concat(hinted.OrderBy(p => p.HintIndex).ThenByDescending(p => p.Sendable))
                     .ToList();
    }

    private static RoutingInfo WithOverride(RoutingInfo entry, RouteConstraints constraints) =>
        constraints.PolicyOverrides.TryGetValue(entry.ShortChannelId, out var policy)
            ? new RoutingInfo(entry.CompactPubKey, entry.ShortChannelId, policy.FeeBaseMsat,
                              policy.FeeProportionalMillionths, policy.CltvExpiryDelta)
            : entry;

    /// <summary>
    /// The graph candidates for <paramref name="amountMsat"/> (see the class remarks), without the paths in
    /// <paramref name="seen"/> (which it extends); none without a graph.
    /// </summary>
    private List<CandidatePath> BuildGraphPaths(PaymentPlanRequest request, ulong amountMsat, HashSet<string> seen)
    {
        var result = new List<CandidatePath>();
        if (request.Graph is not { } graph)
            return result;

        var constraints = request.Constraints;
        var maxCltvDistance = _nodeOptions.Value.Routing.MaxCltvExpiryDistance;
        var finalCltvDelta = checked(request.Target.MinFinalCltvExpiryDelta + HintRouteBuilder.FinalCltvSafetyOffset
                                   + constraints.ExtraCltvDelta);

        // Our usable channels are the first hops, by their live sendable amount (their gossip state never applies)
        var locals = new Dictionary<ShortChannelId, (LocalChannelCandidate Channel, ulong Sendable)>();
        foreach (var channel in request.Channels)
        {
            if (constraints.ExcludedLocalChannels.Contains(channel.ChannelId) || channel.ShortChannelId == default)
                continue;

            var sendable = request.MaxSendableMsat(channel.ChannelId, []);
            if (constraints.LocalLiquidityBoundsMsat.TryGetValue(channel.ChannelId, out var localBound)
             && localBound <= sendable)
                sendable = localBound == 0 ? 0 : localBound - 1;
            if (!locals.TryGetValue(channel.ShortChannelId, out var existing) || existing.Sendable < sendable)
                locals[channel.ShortChannelId] = (channel, sendable);
        }

        if (locals.Count == 0)
            return result;

        var extraEdges = new List<ExtraEdge>();
        foreach (var (scid, local) in locals)
            extraEdges.Add(new ExtraEdge(request.OurNodeId, local.Channel.PeerNodeId, scid, s_ownFirstHopPolicy));

        // The route hints: each entry's node forwards to the next entry's node (the payee after the last)
        foreach (var (_, hintIndex, hintPath) in HintRouteBuilder.GetCandidates(request.Target, request.OurNodeId))
        {
            if (hintIndex < 0)
                continue;

            for (var i = 0; i < hintPath.Count; i++)
            {
                var entry = hintPath[i];
                var to = i + 1 < hintPath.Count ? hintPath[i + 1].CompactPubKey : request.Target.PayeeNodeId;
                if (entry.CompactPubKey == to)
                    continue;

                extraEdges.Add(new ExtraEdge(entry.CompactPubKey, to, entry.ShortChannelId,
                                             HintPolicy(entry, constraints)));
            }
        }

        var excludedNodes = new HashSet<CompactPubKey>(constraints.ExcludedNodes);
        excludedNodes.UnionWith(graph.PenalizedNodes);
        var pathfinding = new PathfindingRequest(request.OurNodeId, request.Target.PayeeNodeId, amountMsat,
                                                 finalCltvDelta)
        {
            MaxTotalCltvDelta = maxCltvDistance,
            MaxFeeMsat = request.MaxFeeMsat,
            ExcludedNodes = excludedNodes,
            ExcludedChannels = constraints.ExcludedChannels,
            PolicyOverrides = constraints.GraphPolicyOverrides,
            ExtraEdges = extraEdges,
            LocalChannels = locals.ToDictionary(pair => pair.Key,
                                                pair => new LocalChannelState(pair.Value.Sendable > 0,
                                                                              pair.Value.Sendable)),
            Liquidity = graph.Liquidity,
            NowUnixSeconds = graph.NowUnixSeconds,
            StaleAfter = graph.StaleAfter,
            CostModel = graph.CostModel
        };

        foreach (var path in _pathfinder.FindPaths(graph.Graph, pathfinding, Math.Max(1, graph.PathsPerAmount)))
        {
            if (!locals.TryGetValue(path.FirstChannel, out var local))
                continue;

            var hops = path.ToRoutingInfos();
            var limits = new List<HopLimit>(hops.Count);
            for (var i = 0; i < hops.Count; i++)
            {
                var policy = path.Hops[i + 1].Policy;
                var capacity = graph.Graph.TryGetChannel(hops[i].ShortChannelId, out var graphChannel)
                                   ? graphChannel.CapacityMsat
                                   : null;
                limits.Add(new HopLimit(policy.HtlcMinimumMsat, policy.HtlcMaximumMsat, capacity));
            }

            var total = (ulong)path.TotalCltvDelta;
            var shadow = total >= maxCltvDistance ? 0 : (uint)Math.Min(graph.ShadowCltvOffset, maxCltvDistance - total);
            var candidate = new CandidatePath(local.Channel, hops,
                                              $"graph route over {local.Channel.ShortChannelId} through "
                                            + $"{string.Join(", ", hops.Select(h => h.ShortChannelId.ToString()))}",
                                              int.MaxValue, local.Sendable, limits, shadow);
            if (seen.Add(Signature(candidate)))
                result.Add(candidate);
        }

        return result;
    }

    private static GraphPolicy HintPolicy(RoutingInfo entry, RouteConstraints constraints)
    {
        if (constraints.PolicyOverrides.TryGetValue(entry.ShortChannelId, out var policy))
            return new GraphPolicy(0, 0, 0, policy.CltvExpiryDelta, policy.HtlcMinimumMsat, policy.HtlcMaximumMsat,
                                   policy.FeeBaseMsat, policy.FeeProportionalMillionths);

        return new GraphPolicy(0, 0, 0, entry.CltvExpiryDelta, 0, ulong.MaxValue, entry.FeeBaseMsat,
                               entry.FeeProportionalMillionths);
    }

    /// <summary>The identity of a path: our channel and the channels after it.</summary>
    private static string Signature(CandidatePath path) =>
        path.Channel.ChannelId + ":" + string.Join(',', path.Hops.Select(h => h.ShortChannelId.ToString()));

    /// <summary>
    /// What one graph hop's channel carries: its origin's <c>htlc_minimum_msat</c> and <c>htlc_maximum_msat</c>, and
    /// the channel's capacity when known.
    /// </summary>
    private readonly record struct HopLimit(ulong HtlcMinimumMsat, ulong HtlcMaximumMsat, ulong? CapacityMsat);

    private sealed record CandidatePath(
        LocalChannelCandidate Channel,
        IReadOnlyList<RoutingInfo> Hops,
        string Description,
        int HintIndex,
        ulong Sendable,
        IReadOnlyList<HopLimit>? Limits = null,
        uint ShadowCltv = 0);
}

/// <summary>
/// One of our channels a payment may start on: <c>Open</c>, with a commitment snapshot and its link up.
/// </summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="PeerNodeId">Our peer on it.</param>
/// <param name="ShortChannelId">Its short channel id (recorded as the first hop's channel).</param>
public sealed record LocalChannelCandidate(ChannelId ChannelId, CompactPubKey PeerNodeId, ShortChannelId ShortChannelId);

/// <summary>
/// One HTLC of a planned round.
/// </summary>
/// <param name="Channel">Our channel to offer it on.</param>
/// <param name="Route">Its route (with <c>total_msat</c>).</param>
/// <param name="Description">Which candidate it follows, for logs and failure reasons.</param>
public sealed record PlannedPart(LocalChannelCandidate Channel, PaymentRoute Route, string Description);

/// <summary>
/// The input of <see cref="PaymentRoutePlanner.TryPlan"/>.
/// </summary>
/// <param name="Target">What to pay.</param>
/// <param name="AmountMsat">What the planned parts must deliver to the payee, together.</param>
/// <param name="TotalMsat">The whole payment's amount (<c>total_msat</c>), at least <paramref name="AmountMsat"/>.</param>
/// <param name="MaxFeeMsat">The most the planned parts may pay in fees, together.</param>
/// <param name="MaxParts">The most parts to plan (1 never splits).</param>
/// <param name="Height">Our best block height.</param>
/// <param name="OurNodeId">Our node id.</param>
/// <param name="Channels">Our usable channels.</param>
/// <param name="MaxSendableMsat">The largest HTLC we may offer on a channel after the given HTLCs (msat) of this plan
/// were added to it.</param>
/// <param name="Constraints">What the payment learnt so far.</param>
/// <param name="MinPartMsat">The smallest part the split plans, unless it is all that is left.</param>
/// <param name="HintForwardsInFlightMsat">What the payment's HTLCs still in flight forward over each route-hint channel
/// (by short channel id; <see cref="PaymentRoutePlanner.SumHintForwards"/>): counted against that channel's
/// <see cref="RouteConstraints.ChannelLiquidityBoundsMsat"/>. Null: none in flight.</param>
/// <param name="Graph">The gossip graph and mission control's estimates for graph paths; null routes only over our
/// direct channels and the route hints.</param>
public sealed record PaymentPlanRequest(
    PaymentTarget Target,
    ulong AmountMsat,
    ulong TotalMsat,
    ulong MaxFeeMsat,
    int MaxParts,
    uint Height,
    CompactPubKey OurNodeId,
    IReadOnlyList<LocalChannelCandidate> Channels,
    Func<ChannelId, IReadOnlyList<ulong>, ulong> MaxSendableMsat,
    RouteConstraints Constraints,
    ulong MinPartMsat,
    IReadOnlyDictionary<ShortChannelId, ulong>? HintForwardsInFlightMsat = null,
    GraphRoutingContext? Graph = null);