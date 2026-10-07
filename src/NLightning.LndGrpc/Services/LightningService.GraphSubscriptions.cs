using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Application.Gossip.Graph;
using Domain.Gossip.Graph;
using Lnrpc;
using Mapping;

public sealed partial class LightningService
{
    /// <summary>Live graph changes from the authoritative graph. No initial snapshot or missed-event replay.</summary>
    public override async Task SubscribeChannelGraph(GraphTopologySubscription request,
                                                      IServerStreamWriter<GraphTopologyUpdate> responseStream,
                                                      ServerCallContext context)
    {
        var store = Graph is not null ? _graphStore! : throw new RpcException(new Status(StatusCode.Unavailable,
            "the gossip graph is disabled on this node"));
        using var queue = new LiveEventQueue<GraphChange>();
        void Changed(object? sender, GraphChange change)
        {
            if (change.Node is not null || (change.Channel is not null
                                         && (change.Removed || change.Channel.SpentAtHeight is null)))
                queue.Publish(change);
        }

        store.GraphChanged += Changed;
        try
        {
            await context.WriteResponseHeadersAsync(new Metadata());
            await queue.WriteToAsync(responseStream, change => ValueTask.FromResult(ToGraphTopologyUpdate(change)),
                                     context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client left.
        }
        finally
        {
            store.GraphChanged -= Changed;
        }
    }

    internal static GraphTopologyUpdate ToGraphTopologyUpdate(GraphChange change)
    {
        var update = new GraphTopologyUpdate();
        if (change.Node is { } node)
        {
            var item = new NodeUpdate
            {
                IdentityKey = node.NodeId.ToString(),
                Alias = node.AliasText,
                Color = node.ColorHex
            };
            item.NodeAddresses.Add(node.Addresses.Select(a => new NodeAddress { Network = "tcp", Addr = a.ToString() }));
            item.Features.Add(LndFeatures.ToMap(node.Features.Span).ToDictionary(p => p.Key, p => p.Value));
            update.NodeUpdates.Add(item);
        }

        if (change.Channel is not { } channel)
            return update;

        var point = change.FundingTxId is { } txId
                        ? new ChannelPoint
                        {
                            FundingTxidStr = txId.ToString(),
                            OutputIndex = channel.ShortChannelId.OutputIndex
                        }
                        : null;
        if (change.Removed)
        {
            update.ClosedChans.Add(new ClosedChannelUpdate
            {
                ChanId = ToChanId(channel.ShortChannelId),
                ChanPoint = point,
                Capacity = (long)((channel.EstimatedCapacityMsat ?? 0) / 1000),
                ClosedHeight = change.ClosedHeight
            });
            return update;
        }

        if (channel.SpentAtHeight is not null)
            return update;

        // LND reports directed edge updates. Announcement-only edges carry no policy; once a policy arrives its
        // actual direction is published. The routing policy selection matches DescribeGraph, including v2 gossip.
        if (change.Policy is { } policy)
            AddEdge(policy.Direction, policy);
        else
        {
            AddEdge(0, channel.GetRoutingPolicy(0));
            AddEdge(1, channel.GetRoutingPolicy(1));
        }
        return update;

        void AddEdge(byte direction, GraphPolicy? policy)
        {
            update.ChannelUpdates.Add(new ChannelEdgeUpdate
            {
                ChanId = ToChanId(channel.ShortChannelId),
                ChanPoint = point?.Clone(),
                Capacity = (long)((channel.EstimatedCapacityMsat ?? 0) / 1000),
                AdvertisingNode = (direction == 0 ? channel.NodeId1 : channel.NodeId2).ToString(),
                ConnectingNode = (direction == 0 ? channel.NodeId2 : channel.NodeId1).ToString(),
                RoutingPolicy = policy is null ? null : ToRoutingPolicy(policy)
            });
        }
    }
}