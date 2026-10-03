namespace NLightning.Domain.Gossip.Interfaces;

using Channels.ValueObjects;

/// <summary>
/// Asks the gossip sync for the current announcement and updates of one channel (BOLT 7 plan G3-T5, decision D9): a
/// payment that failed with an UPDATE failure code learnt that the graph's policy of that channel may be stale. The
/// <c>channel_update</c> carried by the failure is never written to the graph (BOLT 4 "MUST NOT"); the graph changes only
/// through the gossip reply (<c>query_short_channel_ids</c>).
/// </summary>
/// <remarks>
/// Called by the payment retry path under a payment's lock: implementations only record or enqueue and never block. A
/// request may repeat; implementations should coalesce them (one query per channel in flight). The default registration
/// ignores every request.
/// </remarks>
public interface IGossipScidRefresher
{
    /// <summary>
    /// Queues a query for <paramref name="shortChannelId"/>. Returns false when the request was dropped (no gossip
    /// peer, the sync is disabled, or one is already queued).
    /// </summary>
    bool RequestRefresh(ShortChannelId shortChannelId);
}