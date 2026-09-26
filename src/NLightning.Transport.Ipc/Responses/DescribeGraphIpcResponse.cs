using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for DescribeGraph (ClientCommand 20, BOLT 7 plan G5-T4): the graph's counts, memory estimate, write-behind
/// and ingress queues, each connection's sync state, and the requested pages. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class DescribeGraphIpcResponse
{
    [Key(0)] public bool IsLoaded { get; init; }
    [Key(1)] public int Channels { get; init; }
    [Key(2)] public int SpentChannels { get; init; }
    [Key(3)] public int UnverifiedChannels { get; init; }
    [Key(4)] public int OwnChannels { get; init; }
    [Key(5)] public int ChannelsWithoutPolicy { get; init; }
    [Key(6)] public int Policies { get; init; }
    [Key(7)] public int DisabledPolicies { get; init; }
    [Key(8)] public int AnnouncedNodes { get; init; }
    [Key(9)] public int GraphNodes { get; init; }
    [Key(10)] public ulong CapacitySat { get; init; }
    [Key(11)] public int PendingWrites { get; init; }
    [Key(12)] public long EstimatedStoreBytes { get; init; }
    [Key(13)] public long EstimatedSnapshotBytes { get; init; }

    /// <summary>Messages waiting in the ingress; null when the node has no ingress.</summary>
    [Key(14)] public int? IngressQueued { get; init; }

    /// <summary>Messages the ingress dropped (a full queue) since the start.</summary>
    [Key(15)] public long? IngressDropped { get; init; }

    /// <summary>Updates and node announcements waiting for their channel.</summary>
    [Key(16)] public int? Orphans { get; init; }

    /// <summary>A range sync with at least one peer completed; null when the node has no sync manager.</summary>
    [Key(17)] public bool? HasCompletedInitialSync { get; init; }

    [Key(18)] public required List<GraphPeerSyncIpcInfo> Peers { get; init; }

    /// <summary>The requested page of channels, by short channel id.</summary>
    [Key(19)] public required List<GraphChannelIpcInfo> ChannelPage { get; init; }

    /// <summary>The requested page of node announcements, by node id.</summary>
    [Key(20)] public required List<GraphNodeIpcInfo> NodePage { get; init; }

    /// <summary>The offset of the next channel page; null when the page was the last (or not requested).</summary>
    [Key(21)] public int? NextChannelOffset { get; init; }

    /// <summary>The offset of the next node page; null when the page was the last (or not requested).</summary>
    [Key(22)] public int? NextNodeOffset { get; init; }

    public static DescribeGraphIpcResponse FromClientResponse(DescribeGraphClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new DescribeGraphIpcResponse
        {
            IsLoaded = clientResponse.IsLoaded,
            Channels = clientResponse.Channels,
            SpentChannels = clientResponse.SpentChannels,
            UnverifiedChannels = clientResponse.UnverifiedChannels,
            OwnChannels = clientResponse.OwnChannels,
            ChannelsWithoutPolicy = clientResponse.ChannelsWithoutPolicy,
            Policies = clientResponse.Policies,
            DisabledPolicies = clientResponse.DisabledPolicies,
            AnnouncedNodes = clientResponse.AnnouncedNodes,
            GraphNodes = clientResponse.GraphNodes,
            CapacitySat = clientResponse.CapacitySat,
            PendingWrites = clientResponse.PendingWrites,
            EstimatedStoreBytes = clientResponse.EstimatedStoreBytes,
            EstimatedSnapshotBytes = clientResponse.EstimatedSnapshotBytes,
            IngressQueued = clientResponse.IngressQueued,
            IngressDropped = clientResponse.IngressDropped,
            Orphans = clientResponse.Orphans,
            HasCompletedInitialSync = clientResponse.HasCompletedInitialSync,
            Peers = clientResponse.Peers.Select(p => new GraphPeerSyncIpcInfo
            {
                PeerId = p.PeerId,
                SupportsQueries = p.SupportsQueries,
                SupportsQueriesEx = p.SupportsQueriesEx,
                IsSyncPeer = p.IsSyncPeer,
                IsRangeSyncRunning = p.IsRangeSyncRunning,
                LastRangeSyncAt = p.LastRangeSyncAt?.ToUnixTimeSeconds(),
                PeerFilterFirstTimestamp = p.PeerFilterFirstTimestamp,
                PeerFilterTimestampRange = p.PeerFilterTimestampRange,
                OurFilterFirstTimestamp = p.OurFilterFirstTimestamp,
                OurFilterTimestampRange = p.OurFilterTimestampRange,
                QueryingStopped = p.QueryingStopped,
                PendingWork = p.PendingWork
            }).ToList(),
            ChannelPage = clientResponse.ChannelPage.Select(GraphChannelIpcInfo.From).ToList(),
            NodePage = clientResponse.NodePage.Select(GraphNodeIpcInfo.From).ToList(),
            NextChannelOffset = clientResponse.NextChannelOffset,
            NextNodeOffset = clientResponse.NextNodeOffset
        };
    }
}

/// <summary>One connection's gossip sync state in a <see cref="DescribeGraphIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class GraphPeerSyncIpcInfo
{
    [Key(0)] public required CompactPubKey PeerId { get; init; }
    [Key(1)] public bool SupportsQueries { get; init; }
    [Key(2)] public bool SupportsQueriesEx { get; init; }
    [Key(3)] public bool IsSyncPeer { get; init; }
    [Key(4)] public bool IsRangeSyncRunning { get; init; }

    /// <summary>When the last range sync with the peer completed (UNIX seconds).</summary>
    [Key(5)] public long? LastRangeSyncAt { get; init; }

    /// <summary>The <c>first_timestamp</c> of the peer's filter, if it sent one.</summary>
    [Key(6)] public uint? PeerFilterFirstTimestamp { get; init; }

    /// <summary>The <c>timestamp_range</c> of the peer's filter, if it sent one.</summary>
    [Key(7)] public uint? PeerFilterTimestampRange { get; init; }

    /// <summary>The <c>first_timestamp</c> of the last filter we sent the connection, if any.</summary>
    [Key(8)] public uint? OurFilterFirstTimestamp { get; init; }

    /// <summary>The <c>timestamp_range</c> of the last filter we sent the connection, if any.</summary>
    [Key(9)] public uint? OurFilterTimestampRange { get; init; }

    /// <summary>A query of ours failed, so nothing more is asked on this connection.</summary>
    [Key(10)] public bool QueryingStopped { get; init; }

    [Key(11)] public int PendingWork { get; init; }
}