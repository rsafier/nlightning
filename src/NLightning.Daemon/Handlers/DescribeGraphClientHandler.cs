using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Gossip.Graph;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Options;
using Interfaces;

/// <summary>
/// Describes the gossip graph (ClientCommand 20, BOLT 7 plan G5-T4): the counts, the store's memory estimate and
/// pending writes, the ingress queue, dropped messages and orphans, and each connection's sync state; on request one
/// page of channels (by short channel id) and one of node announcements (by node id), at most
/// <see cref="DescribeGraphClientRequest.MaxLimit"/> each. Refused with <c>invalid_operation</c> while the graph is
/// disabled, and for a negative offset or a limit outside 1 to the maximum.
/// </summary>
public sealed class DescribeGraphClientHandler
    : IClientCommandHandler<DescribeGraphClientRequest, DescribeGraphClientResponse>
{
    private readonly GossipGraphDescriber _describer;
    private readonly GossipGraphOptions _options;
    private readonly NodeOptions _nodeOptions;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.DescribeGraph;

    public DescribeGraphClientHandler(GossipGraphDescriber describer, IOptions<GossipGraphOptions> options,
                                      IOptions<NodeOptions> nodeOptions)
    {
        _describer = describer;
        _options = options.Value;
        _nodeOptions = nodeOptions.Value;
    }

    /// <inheritdoc/>
    public Task<DescribeGraphClientResponse> HandleAsync(DescribeGraphClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphClientGuards.ThrowIfDisabled(_options, _nodeOptions);
        if (request.Offset < 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The offset cannot be negative");
        if (request.Limit is < 1 or > DescribeGraphClientRequest.MaxLimit)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The limit must be between 1 and {DescribeGraphClientRequest.MaxLimit}");

        if (request.Offset > 0 && request.IncludeChannels && request.IncludeNodes)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "An offset pages one listing: ask for the channels or the nodes, not both");

        var description = _describer.Describe();
        var snapshot = description.Snapshot;
        var response = new DescribeGraphClientResponse
        {
            IsLoaded = description.IsLoaded,
            Channels = description.Channels,
            SpentChannels = description.SpentChannels,
            UnverifiedChannels = description.UnverifiedChannels,
            OwnChannels = description.OwnChannels,
            ChannelsWithoutPolicy = description.ChannelsWithoutPolicy,
            Policies = description.Policies,
            DisabledPolicies = description.DisabledPolicies,
            AnnouncedNodes = description.AnnouncedNodes,
            GraphNodes = description.GraphNodes,
            CapacitySat = description.CapacitySat,
            PendingWrites = description.PendingWrites,
            EstimatedStoreBytes = description.Memory.StoreBytes,
            EstimatedSnapshotBytes = description.Memory.SnapshotBytes,
            IngressQueued = description.Ingress?.QueuedMessages,
            IngressDropped = description.Ingress?.DroppedMessages,
            Orphans = description.Ingress?.Orphans,
            HasCompletedInitialSync = description.Sync?.HasCompletedInitialSync,
            Peers = description.Sync?.Peers
                               .OrderBy(p => p.PeerId, GraphClientGuards.NodeIdComparer)
                               .Select(p => new GraphPeerSyncInfo(p.PeerId, p.SupportsQueries, p.SupportsQueriesEx,
                                                                  p.IsSyncPeer, p.IsRangeSyncRunning,
                                                                  p.LastRangeSyncAt, p.PeerFilter?.FirstTimestamp,
                                                                  p.PeerFilter?.TimestampRange,
                                                                  p.OurFilter?.FirstTimestamp,
                                                                  p.OurFilter?.TimestampRange,
                                                                  p.IsQuerySlotPoisoned, p.PendingWork))
                               .ToList() ?? []
        };

        if (request.IncludeChannels)
        {
            var page = snapshot.Channels
                               .OrderBy(c => c.ShortChannelId.BlockHeight)
                               .ThenBy(c => c.ShortChannelId.TransactionIndex)
                               .ThenBy(c => c.ShortChannelId.OutputIndex)
                               .Skip(request.Offset)
                               .Take(request.Limit)
                               .ToList();
            response = response with
            {
                ChannelPage = page,
                NextChannelOffset = NextOffset(request, page.Count, snapshot.ChannelCount)
            };
        }

        if (request.IncludeNodes)
        {
            var channelCounts = GraphClientGuards.CountChannels(snapshot);
            var nodes = snapshot.Nodes.ToList();
            var page = nodes.OrderBy(n => n.NodeId, GraphClientGuards.NodeIdComparer)
                            .Skip(request.Offset)
                            .Take(request.Limit)
                            .Select(n => new GraphNodeInfo(n, channelCounts.GetValueOrDefault(n.NodeId)))
                            .ToList();
            response = response with
            {
                NodePage = page,
                NextNodeOffset = NextOffset(request, page.Count, nodes.Count)
            };
        }

        return Task.FromResult(response);
    }

    private static int? NextOffset(DescribeGraphClientRequest request, int pageCount, int total)
    {
        var next = request.Offset + pageCount;
        return pageCount > 0 && next < total ? next : null;
    }
}