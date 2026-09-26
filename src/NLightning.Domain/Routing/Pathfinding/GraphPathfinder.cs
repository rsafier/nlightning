namespace NLightning.Domain.Routing.Pathfinding;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Gossip.Graph;
using Payments.Policies;

/// <summary>
/// The pure pathfinder (plan BOLT7 §3.10, G4-T1, decision D6): Dijkstra run <b>backward from the payee</b>, so the
/// amount and CLTV each hop must receive are exact when its edge is relaxed.
/// </summary>
/// <remarks>
/// <para>Relaxing the edge <c>u → v</c> (the policy of <c>u</c> on that channel) with <c>v</c> needing
/// <c>amount(v)</c> and <c>cltv(v)</c>: <c>u</c> must receive <c>amount(v) + fee(amount(v))</c> (BOLT 7 "HTLC Fees",
/// <see cref="ForwardingFee"/>) and <c>cltv(v) + cltv_expiry_delta</c>; when <c>u</c> is us, nothing is added. The
/// HTLC over the edge carries <c>amount(v)</c>, which must lie in <c>[htlc_minimum_msat, htlc_maximum_msat]</c>
/// and within the capacity. The edge cost is <see cref="PathCostModel.EdgeCost"/>.</para>
/// <para>Skipped: disabled directions (our own channel uses its live state instead), a policy with
/// <c>htlc_maximum_msat</c> above the capacity (B7-CU-03), spent channels, stale channels (when asked), channels and
/// intermediate nodes with unknown even features (B7-CA-03, B7-NA-04), excluded nodes/channels/directions, paths
/// over <see cref="PathfindingRequest.MaxHops"/>, <see cref="PathfindingRequest.MaxTotalCltvDelta"/> or
/// <see cref="PathfindingRequest.MaxFeeMsat"/>, and edges below <see cref="PathCostModel.MinProbability"/>.</para>
/// <para>Diverse paths (<see cref="FindPaths"/>): Dijkstra again with every edge of the earlier paths penalized by
/// <see cref="PathCostModel.DiversityPenalty"/> per earlier use (a cheap alternative to Yen's k-shortest paths).</para>
/// <para>Thread-safe: it keeps no state between calls; the graph view is immutable.</para>
/// </remarks>
public sealed class GraphPathfinder
{
    /// <summary>
    /// The cheapest path, or null when none satisfies the request.
    /// </summary>
    /// <exception cref="ArgumentException">The source is the target, or the amount is zero.</exception>
    public GraphPath? FindPath(IGraphView graph, PathfindingRequest request) =>
        FindPaths(graph, request, 1).FirstOrDefault();

    /// <summary>
    /// Up to <paramref name="count"/> distinct paths, cheapest first; later ones avoid the edges of earlier ones where
    /// that is not too costly.
    /// </summary>
    /// <exception cref="ArgumentException">The source is the target, or the amount is zero.</exception>
    public IReadOnlyList<GraphPath> FindPaths(IGraphView graph, PathfindingRequest request, int count)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (request.Source == request.Target)
            throw new ArgumentException("The source is the target.", nameof(request));
        if (request.AmountMsat == 0)
            throw new ArgumentException("The amount must be positive.", nameof(request));

        var search = new Search(graph, request);
        var paths = new List<GraphPath>();
        var signatures = new HashSet<string>();
        var usage = new Dictionary<DirectedChannel, int>();

        // Each round either adds a path or penalizes a duplicate further; bound the rounds
        for (var round = 0; round < 2 * count && paths.Count < count; round++)
        {
            var path = search.Run(usage);
            if (path is null)
                break;

            foreach (var edge in path.Edges)
                usage[edge] = usage.GetValueOrDefault(edge) + 1;

            if (signatures.Add(string.Join(',', path.Edges.Select(e => $"{e.ShortChannelId}/{e.Direction}"))))
                paths.Add(path.Path);
        }

        return paths;
    }

    private sealed record FoundPath(GraphPath Path, IReadOnlyList<DirectedChannel> Edges);

    /// <summary>
    /// One incoming edge <c>From → To</c> as the search relaxes it.
    /// </summary>
    private readonly record struct Edge(int From, ShortChannelId ShortChannelId, byte Direction, GraphPolicy? Policy,
                                        ulong? CapacityMsat, GraphChannel? Channel);

    private sealed class Search
    {
        private readonly IGraphView _graph;
        private readonly PathfindingRequest _request;
        private readonly PathCostModel _model;
        private readonly int _sourceIndex;
        private readonly int _targetIndex;
        private readonly int _nodeCount;
        private readonly List<CompactPubKey> _extraNodeIds = [];
        private readonly Dictionary<CompactPubKey, int> _extraIndexByNode = new();
        private readonly Dictionary<int, List<Edge>> _extraIncoming = new();
        private readonly bool _targetUsable;

        public Search(IGraphView graph, PathfindingRequest request)
        {
            _graph = graph;
            _request = request;
            _model = request.CostModel ?? PathCostModel.Default;
            _nodeCount = graph.NodeCount;

            foreach (var extra in request.ExtraEdges ?? [])
            {
                var from = GetIndex(extra.From);
                var to = GetIndex(extra.To);
                if (from == to)
                    continue;

                if (!_extraIncoming.TryGetValue(to, out var list))
                    _extraIncoming[to] = list = [];

                var directed = DirectedChannel.Between(extra.ShortChannelId, extra.From, extra.To);
                list.Add(new Edge(from, extra.ShortChannelId, directed.Direction, extra.Policy, extra.CapacityMsat,
                                  null));
            }

            _sourceIndex = GetIndex(request.Source);
            _targetIndex = GetIndex(request.Target);
            _targetUsable = request.AllowTargetUnknownFeatures || !IsNodeWithUnknownEvenFeatures(_targetIndex);
        }

        private int TotalNodes => _nodeCount + _extraNodeIds.Count;

        public FoundPath? Run(IReadOnlyDictionary<DirectedChannel, int> usage)
        {
            if (!_targetUsable)
                return null;

            var total = TotalNodes;
            var cost = new double[total];
            Array.Fill(cost, double.PositiveInfinity);
            var amount = new ulong[total];
            var cltv = new uint[total];
            var hops = new int[total];
            var next = new int[total];
            var nextEdge = new Edge[total];
            var probability = new double[total];
            var settled = new bool[total];

            cost[_targetIndex] = 0;
            amount[_targetIndex] = _request.AmountMsat;
            cltv[_targetIndex] = _request.FinalCltvDelta;
            next[_targetIndex] = -1;
            probability[_targetIndex] = 1;
            if (_request.FinalCltvDelta > _request.MaxTotalCltvDelta)
                return null;

            var queue = new PriorityQueue<int, double>();
            queue.Enqueue(_targetIndex, 0);
            while (queue.TryDequeue(out var v, out var queuedCost))
            {
                if (settled[v] || queuedCost > cost[v])
                    continue;

                settled[v] = true;
                if (v == _sourceIndex)
                    break;

                // A node other than the payee forwards: it must be allowed to
                if (v != _targetIndex && !CanForwardThrough(v))
                    continue;

                if (hops[v] + 1 > _request.MaxHops)
                    continue;

                foreach (var edge in GetIncoming(v))
                {
                    var u = edge.From;
                    if (settled[u] || u == _targetIndex)
                        continue;

                    if (!TryRelax(edge, amount[v], cltv[v], out var newAmount, out var newCltv, out var feeMsat,
                                  out var edgeProbability, out var cltvDelta))
                        continue;

                    var directed = new DirectedChannel(edge.ShortChannelId, edge.Direction);
                    var edgeCost = _model.EdgeCost(feeMsat, amount[v], cltvDelta, edgeProbability)
                                 + _model.DiversityPenalty(usage.GetValueOrDefault(directed), amount[v]);
                    var newCost = cost[v] + edgeCost;
                    if (newCost >= cost[u])
                        continue;

                    cost[u] = newCost;
                    amount[u] = newAmount;
                    cltv[u] = newCltv;
                    hops[u] = hops[v] + 1;
                    next[u] = v;
                    nextEdge[u] = edge;
                    probability[u] = edgeProbability;
                    queue.Enqueue(u, newCost);
                }
            }

            if (!settled[_sourceIndex])
                return null;

            return BuildPath(cost, amount, cltv, next, nextEdge, probability);
        }

        private FoundPath BuildPath(double[] cost, ulong[] amount, uint[] cltv, int[] next, Edge[] nextEdge,
                                    double[] probability)
        {
            var shadow = Math.Min(_request.ShadowCltvOffset, _request.MaxTotalCltvDelta - cltv[_sourceIndex]);
            var pathHops = new List<PathHop>();
            var edges = new List<DirectedChannel>();
            var node = _sourceIndex;
            while (node != _targetIndex)
            {
                var edge = nextEdge[node];
                var to = next[node];
                var fee = to == _targetIndex ? 0 : amount[to] - amount[next[to]];
                pathHops.Add(new PathHop(GetNodeId(to), edge.ShortChannelId, amount[to], cltv[to] + shadow, fee,
                                         EffectivePolicy(edge)!, probability[node]));
                edges.Add(new DirectedChannel(edge.ShortChannelId, edge.Direction));
                node = to;
            }

            return new FoundPath(new GraphPath(pathHops, cost[_sourceIndex], shadow), edges);
        }

        private bool TryRelax(Edge edge, ulong amountAtV, uint cltvAtV, out ulong newAmount, out uint newCltv,
                              out ulong feeMsat, out double edgeProbability, out uint cltvDelta)
        {
            newAmount = 0;
            newCltv = 0;
            feeMsat = 0;
            edgeProbability = 0;
            cltvDelta = 0;

            var directed = new DirectedChannel(edge.ShortChannelId, edge.Direction);
            if (_request.ExcludedChannels?.Contains(edge.ShortChannelId) == true
             || _request.ExcludedEdges?.Contains(directed) == true)
                return false;

            var channel = edge.Channel;
            if (channel is not null)
            {
                if (channel.SpentAtHeight is not null || channel.HasUnknownEvenFeatures)
                    return false;
                if (_request.StaleAfter is { } staleAfter && channel.IsStale(_request.NowUnixSeconds, staleAfter))
                    return false;
            }

            var policy = EffectivePolicy(edge);
            if (policy is null)
                return false;

            var fromSource = edge.From == _sourceIndex;
            LocalChannelState? local = null;
            if (fromSource && _request.LocalChannels is { } localChannels)
            {
                if (!localChannels.TryGetValue(edge.ShortChannelId, out local) || !local.IsUsable
                                                                               || amountAtV > local.SpendableMsat
                                                                               || amountAtV < local.HtlcMinimumMsat)
                    return false;
            }
            else if (policy.IsDisabled)
            {
                return false;
            }

            if (amountAtV < policy.HtlcMinimumMsat || amountAtV > policy.HtlcMaximumMsat)
                return false;

            if (edge.CapacityMsat is { } capacity && (policy.HtlcMaximumMsat > capacity || amountAtV > capacity))
                return false;

            if (fromSource)
            {
                newAmount = amountAtV;
                newCltv = cltvAtV;
            }
            else
            {
                if (!CanForwardThrough(edge.From))
                    return false;

                try
                {
                    feeMsat = ForwardingFee.CalculateMsat(policy.FeeBaseMsat, policy.FeeProportionalMillionths,
                                                          amountAtV);
                    newAmount = checked(amountAtV + feeMsat);
                }
                catch (OverflowException)
                {
                    return false;
                }

                cltvDelta = policy.CltvExpiryDelta;
                newCltv = cltvAtV + cltvDelta;
            }

            if (newCltv > _request.MaxTotalCltvDelta)
                return false;
            if (_request.MaxFeeMsat is { } maxFee && newAmount - _request.AmountMsat > maxFee)
                return false;

            if (local is not null)
            {
                edgeProbability = 1;
            }
            else
            {
                edgeProbability = _request.Liquidity?.GetSuccessProbability(directed, amountAtV, edge.CapacityMsat,
                                                                           _request.NowUnixSeconds,
                                                                           _model.AprioriProbability)
                               ?? (edge.CapacityMsat is { } cap && amountAtV > cap ? 0 : _model.AprioriProbability);
                if (channel?.Verification == GraphChannelVerification.Unverified)
                    edgeProbability *= _model.UnverifiedProbabilityFactor;
            }

            return edgeProbability >= _model.MinProbability;
        }

        private GraphPolicy? EffectivePolicy(Edge edge)
        {
            if (_request.PolicyOverrides is { } overrides
             && overrides.TryGetValue(new DirectedChannel(edge.ShortChannelId, edge.Direction), out var overridden))
                return overridden;

            return edge.Policy;
        }

        private IEnumerable<Edge> GetIncoming(int v)
        {
            if (v < _nodeCount)
            {
                foreach (var adjacency in _graph.GetAdjacency(v))
                {
                    var neighborDirection = (byte)(1 - adjacency.LocalDirection);
                    yield return new Edge(adjacency.NeighborIndex, adjacency.Channel.ShortChannelId, neighborDirection,
                                          adjacency.IncomingPolicy, adjacency.Channel.CapacityMsat, adjacency.Channel);
                }
            }

            if (_extraIncoming.TryGetValue(v, out var extras))
                foreach (var edge in extras)
                    yield return edge;
        }

        private bool CanForwardThrough(int index)
        {
            if (index == _sourceIndex)
                return true;

            var nodeId = GetNodeId(index);
            if (_request.ExcludedNodes?.Contains(nodeId) == true)
                return false;

            return !IsNodeWithUnknownEvenFeatures(index);
        }

        private bool IsNodeWithUnknownEvenFeatures(int index) =>
            index < _nodeCount && _graph.GetNode(index)?.HasUnknownEvenFeatures == true;

        private CompactPubKey GetNodeId(int index) =>
            index < _nodeCount ? _graph.GetNodeId(index) : _extraNodeIds[index - _nodeCount];

        private int GetIndex(CompactPubKey nodeId)
        {
            if (_graph.TryGetNodeIndex(nodeId, out var index))
                return index;
            if (_extraIndexByNode.TryGetValue(nodeId, out index))
                return index;

            index = _nodeCount + _extraNodeIds.Count;
            _extraNodeIds.Add(nodeId);
            _extraIndexByNode.Add(nodeId, index);
            return index;
        }
    }
}