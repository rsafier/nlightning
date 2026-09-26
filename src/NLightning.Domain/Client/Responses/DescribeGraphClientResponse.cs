namespace NLightning.Domain.Client.Responses;

using Crypto.ValueObjects;
using Gossip.Graph;

/// <summary>
/// The state of the gossip graph (<c>ClientCommand.DescribeGraph</c>, BOLT 7 plan G5-T4): counts, memory, the
/// write-behind and ingress queues, each connection's sync state, and the requested pages.
/// </summary>
public sealed record DescribeGraphClientResponse
{
    /// <summary>The store loaded the persisted graph.</summary>
    public required bool IsLoaded { get; init; }

    /// <summary>Channels, spent ones included.</summary>
    public required int Channels { get; init; }

    /// <summary>Channels whose funding output is spent (removed 72 blocks after the spend).</summary>
    public required int SpentChannels { get; init; }

    /// <summary>Channels kept without a funding check.</summary>
    public required int UnverifiedChannels { get; init; }

    /// <summary>Our own announced channels.</summary>
    public required int OwnChannels { get; init; }

    /// <summary>Channels without a <c>channel_update</c> in either direction.</summary>
    public required int ChannelsWithoutPolicy { get; init; }

    /// <summary>Stored <c>channel_update</c> directions.</summary>
    public required int Policies { get; init; }

    /// <summary>Directions with the <c>disable</c> bit set.</summary>
    public required int DisabledPolicies { get; init; }

    /// <summary>Nodes with a <c>node_announcement</c>.</summary>
    public required int AnnouncedNodes { get; init; }

    /// <summary>Nodes the graph knows (channel ends and announced nodes).</summary>
    public required int GraphNodes { get; init; }

    /// <summary>The summed capacity of the channels with a known capacity.</summary>
    public required ulong CapacitySat { get; init; }

    /// <summary>Graph changes not written to the database yet.</summary>
    public required int PendingWrites { get; init; }

    /// <summary>The store's estimated managed memory.</summary>
    public required long EstimatedStoreBytes { get; init; }

    /// <summary>The estimated memory of one snapshot.</summary>
    public required long EstimatedSnapshotBytes { get; init; }

    /// <summary>Messages waiting in the gossip ingress, or null without an ingress.</summary>
    public int? IngressQueued { get; init; }

    /// <summary>Messages the ingress dropped because a queue was full, since the start.</summary>
    public long? IngressDropped { get; init; }

    /// <summary>Updates and node announcements waiting for their channel.</summary>
    public int? Orphans { get; init; }

    /// <summary>A range sync with at least one peer completed, or null without a sync manager.</summary>
    public bool? HasCompletedInitialSync { get; init; }

    /// <summary>Each connection's sync state, by node id.</summary>
    public IReadOnlyList<GraphPeerSyncInfo> Peers { get; init; } = [];

    /// <summary>The requested page of channels, by short channel id (empty when not requested).</summary>
    public IReadOnlyList<GraphChannel> ChannelPage { get; init; } = [];

    /// <summary>The requested page of node announcements, by node id (empty when not requested).</summary>
    public IReadOnlyList<GraphNodeInfo> NodePage { get; init; } = [];

    /// <summary>The offset of the next channel page, or null when this page is the last.</summary>
    public int? NextChannelOffset { get; init; }

    /// <summary>The offset of the next node page, or null when this page is the last.</summary>
    public int? NextNodeOffset { get; init; }
}

/// <summary>The gossip sync state of one connection.</summary>
/// <param name="PeerId">The peer's node id.</param>
/// <param name="SupportsQueries"><c>gossip_queries</c> is negotiated.</param>
/// <param name="SupportsQueriesEx"><c>gossip_queries_ex</c> is negotiated.</param>
/// <param name="IsSyncPeer">One of the peers we sync the graph from.</param>
/// <param name="IsRangeSyncRunning">A range sync with it is in progress.</param>
/// <param name="LastRangeSyncAt">When the last range sync with it completed.</param>
/// <param name="PeerFilterFirstTimestamp">The <c>first_timestamp</c> of its filter, if it sent one.</param>
/// <param name="PeerFilterTimestampRange">The <c>timestamp_range</c> of its filter, if it sent one.</param>
/// <param name="OurFilterFirstTimestamp">The <c>first_timestamp</c> of the last filter we sent it, if any.</param>
/// <param name="OurFilterTimestampRange">The <c>timestamp_range</c> of the last filter we sent it, if any.</param>
/// <param name="QueryingStopped">A query of ours failed, so nothing more is asked on this connection.</param>
/// <param name="PendingWork">Queued sync work.</param>
public sealed record GraphPeerSyncInfo(
    CompactPubKey PeerId,
    bool SupportsQueries,
    bool SupportsQueriesEx,
    bool IsSyncPeer,
    bool IsRangeSyncRunning,
    DateTimeOffset? LastRangeSyncAt,
    uint? PeerFilterFirstTimestamp,
    uint? PeerFilterTimestampRange,
    uint? OurFilterFirstTimestamp,
    uint? OurFilterTimestampRange,
    bool QueryingStopped,
    int PendingWork);