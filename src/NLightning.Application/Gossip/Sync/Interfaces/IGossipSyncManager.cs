namespace NLightning.Application.Gossip.Sync.Interfaces;

using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;

/// <summary>
/// The gossip sync of the node (plan BOLT7 §3.7, G3-T1/G3-T2): answers peers' queries, syncs the graph from up to
/// <see cref="GossipSyncOptions.SyncPeers"/> peers through queries, sends the <c>gossip_timestamp_filter</c>s, keeps the
/// peers' filters, and re-queries what the ingress dropped (NL-353).
/// </summary>
public interface IGossipSyncManager : IGossipSyncService
{
    /// <summary>
    /// True once a range sync with at least one peer completed (every reply valid and every query answered): our
    /// <c>reply_short_channel_ids_end</c> then carries <c>full_information</c> = 1.
    /// </summary>
    bool HasCompletedInitialSync { get; }

    /// <summary>
    /// Raised when a peer sent a <c>gossip_timestamp_filter</c> for our chain (the relay's start signal, B7-RL-01).
    /// </summary>
    event EventHandler<GossipFilterReceivedEventArgs>? FilterReceived;

    /// <summary>
    /// The latest <c>gossip_timestamp_filter</c> of the connection <paramref name="peer"/>; false while it sent none
    /// (BOLT 7: no relayed gossip goes to it until then).
    /// </summary>
    bool TryGetPeerFilter(IPeerService peer, out GossipTimestampFilter filter);

    /// <summary>
    /// Asks a connected <c>gossip_queries</c> peer for the announcement and updates of one channel
    /// (<c>query_short_channel_ids</c>, one outstanding query per peer; e.g. after a payment failure named a channel
    /// whose update we lack, plan D9/G3-T5). What comes back goes through the ingress like any gossip.
    /// </summary>
    /// <returns>
    /// True once the peer answered (<c>reply_short_channel_ids_end</c>); false when no peer can be asked, the channel
    /// is known spent, or the connection or the wait ended first.
    /// </returns>
    Task<bool> QueryScidAsync(ShortChannelId shortChannelId, CancellationToken cancellationToken = default);
}

/// <summary>A peer's <c>gossip_timestamp_filter</c>.</summary>
public sealed class GossipFilterReceivedEventArgs(IPeerService peer, GossipTimestampFilter filter) : EventArgs
{
    /// <summary>The connection that sent it.</summary>
    public IPeerService Peer { get; } = peer;

    /// <summary>The filter.</summary>
    public GossipTimestampFilter Filter { get; } = filter;
}