namespace NLightning.Application.Gossip.Sync;

using Domain.Crypto.ValueObjects;
using Domain.Gossip.Queries;

/// <summary>
/// The gossip sync state of one connection, as <see cref="GossipSyncManager.GetPeerStates"/> reads it (BOLT 7 plan
/// G5-T4 <c>describegraph</c>).
/// </summary>
/// <param name="PeerId">The peer's node id.</param>
/// <param name="IsInitialized">Both <c>init</c>s were exchanged (the session's loops run).</param>
/// <param name="SupportsQueries"><c>gossip_queries</c> is negotiated.</param>
/// <param name="SupportsQueriesEx"><c>gossip_queries_ex</c> is negotiated.</param>
/// <param name="IsSyncPeer">One of the <c>Gossip:SyncPeers</c> we sync the graph from.</param>
/// <param name="IsRangeSyncRunning">A range sync with this peer is in progress.</param>
/// <param name="LastRangeSyncAt">When the last range sync with this peer completed, if one did.</param>
/// <param name="PeerFilter">The peer's latest <c>gossip_timestamp_filter</c> for our chain, if any.</param>
/// <param name="OurFilter">The last <c>gossip_timestamp_filter</c> we sent this connection, if any.</param>
/// <param name="IsQuerySlotPoisoned">
/// A query of ours went unanswered or broke the rules, so nothing more is asked on this connection.
/// </param>
/// <param name="PendingWork">Queued sync work items (range syncs, SCID queries, filters).</param>
public sealed record GossipSyncPeerState(
    CompactPubKey PeerId,
    bool IsInitialized,
    bool SupportsQueries,
    bool SupportsQueriesEx,
    bool IsSyncPeer,
    bool IsRangeSyncRunning,
    DateTimeOffset? LastRangeSyncAt,
    GossipTimestampFilter? PeerFilter,
    GossipTimestampFilter? OurFilter,
    bool IsQuerySlotPoisoned,
    int PendingWork);