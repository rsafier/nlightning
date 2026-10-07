using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>GetNetworkInfo</c> (NL-1246) from the gossip graph, computed as LND's <c>rpcserver.GetNetworkInfo</c>: every
    /// node that is a channel end or announced itself is a node; every unspent channel that is not a zombie counts once
    /// (its capacity from the funding output, or the estimate when it was not looked up); a node's out degree is its
    /// number of such channels; <c>avg_out_degree</c> = 2 x channels / nodes; min/max/avg/median capacity (the median of
    /// an even count is the mean of the two middle ones). <c>num_zombie_chans</c> counts the channels whose policy
    /// is past the stale rule (<c>Gossip:StaleAfter</c>, BOLT 7's two weeks; LND marks those zombies and leaves them out
    /// of its graph, so they are left out of the other figures too). <c>graph_diameter</c> is 0 (not computed; LND
    /// often reports 0 for large graphs). No graph: <c>UNAVAILABLE</c>, as <c>DescribeGraph</c>.
    /// </summary>
    public override Task<NetworkInfo> GetNetworkInfo(NetworkInfoRequest request, ServerCallContext context)
    {
        var graph = Graph ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                               "the gossip graph is disabled on this node"));
        var now = (ulong)Math.Max(0, _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var staleAfter = _graphOptions?.StaleAfter ?? TimeSpan.FromDays(14);
        var tip = _blockchainMonitor?.LastProcessedBlockHeight is { } height and > 0 ? height : (uint?)null;
        return Task.FromResult(ComputeNetworkInfo(graph, now, staleAfter, tip));
    }

    /// <summary>The figures of <see cref="GetNetworkInfo"/> for one graph snapshot.</summary>
    internal static NetworkInfo ComputeNetworkInfo(IGraphView graph, ulong nowUnixSeconds, TimeSpan staleAfter,
                                                   uint? tipHeight)
    {
        var nodes = new HashSet<CompactPubKey>(graph.Nodes.Select(n => n.NodeId));
        var degrees = new Dictionary<CompactPubKey, uint>();
        var capacities = new List<long>();
        ulong zombies = 0;
        foreach (var channel in graph.Channels)
        {
            if (channel.SpentAtHeight is not null)
                continue;

            var hasPolicy = channel.GetRoutingPolicy(0) is not null || channel.GetRoutingPolicy(1) is not null;
            if (hasPolicy && channel.IsStale(nowUnixSeconds, staleAfter, tipHeight))
            {
                zombies++;
                continue;
            }

            capacities.Add((long)((channel.CapacityMsat ?? channel.EstimatedCapacityMsat ?? 0) / 1_000));
            foreach (var end in new[] { channel.NodeId1, channel.NodeId2 })
            {
                nodes.Add(end);
                degrees[end] = degrees.GetValueOrDefault(end) + 1;
            }
        }

        var info = new NetworkInfo
        {
            GraphDiameter = 0,
            NumNodes = (uint)nodes.Count,
            NumChannels = (uint)capacities.Count,
            MaxOutDegree = degrees.Count == 0 ? 0 : degrees.Values.Max(),
            NumZombieChans = zombies
        };
        if (nodes.Count > 0)
            info.AvgOutDegree = 2.0 * capacities.Count / nodes.Count;
        if (capacities.Count == 0)
            return info;

        capacities.Sort();
        var total = capacities.Sum();
        info.TotalNetworkCapacity = total;
        info.AvgChannelSize = (double)total / capacities.Count;
        info.MinChannelSize = capacities[0];
        info.MaxChannelSize = capacities[^1];
        var middle = capacities.Count / 2;
        info.MedianChannelSizeSat = capacities.Count % 2 == 1
                                        ? capacities[middle]
                                        : (capacities[middle - 1] + capacities[middle]) / 2;
        return info;
    }
}