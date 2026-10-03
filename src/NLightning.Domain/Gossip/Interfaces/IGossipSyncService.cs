namespace NLightning.Domain.Gossip.Interfaces;

using Node.Interfaces;
using Protocol.Interfaces;

/// <summary>
/// The BOLT 7 gossip query side of the peer connections (plan BOLT7 §3.7, G3-T1/G3-T2): it answers a peer's
/// <c>query_channel_range</c> and <c>query_short_channel_ids</c> from the graph, runs our own queries (the sync), sends
/// our <c>gossip_timestamp_filter</c>, and keeps the peer's filter for the relay. The peer service hands it every
/// message of types 261-265 and tells it once the <c>init</c> exchange is done.
/// </summary>
public interface IGossipSyncService
{
    /// <summary>
    /// Called once per connection, after both <c>init</c> messages went out (BOLT 1: nothing precedes init). Decides
    /// whether we sync from the peer (range query), only send it a <c>gossip_timestamp_filter</c>, or send the
    /// "nothing" filter of a peer without <c>gossip_queries</c>. Never blocks and never throws.
    /// </summary>
    void OnPeerInitialized(IPeerService peer);

    /// <summary>
    /// Handles a <c>query_short_channel_ids</c>, <c>reply_short_channel_ids_end</c>, <c>query_channel_range</c>,
    /// <c>reply_channel_range</c> or <c>gossip_timestamp_filter</c> from <paramref name="peer"/>. Runs on the transport
    /// read loop, so it only queues work: replies go out in order per peer, and a malformed query or reply gets a
    /// <c>warning</c>. Never blocks and never throws.
    /// </summary>
    void HandleMessage(IPeerService peer, IMessage message);
}