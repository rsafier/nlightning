namespace NLightning.Domain.Gossip.Interfaces;

using Channels.ValueObjects;

/// <summary>
/// A read-only view of the short channel ids whose <c>channel_announcement</c> is already on its way into the graph
/// (NL-415, NL-414): queued or deferred in the gossip ingress, or waiting for or inside its funding output lookup. The
/// gossip sync does not ask another peer for such a channel: its answer would only be downloaded and checked twice.
/// </summary>
/// <remarks>
/// Implementations must answer from memory, without I/O and without blocking (the sync asks for every channel of a
/// batch), and may be approximate: a false "pending" only delays a channel until the next sync or the missed-channel
/// retry, a false "not pending" only costs a duplicate download.
/// </remarks>
public interface IGossipPendingChannels
{
    /// <summary>
    /// True while the announcement of <paramref name="shortChannelId"/> is queued, deferred or being looked up on
    /// chain, or while its funding output is known to be spent by a mempool transaction and no block came since (its
    /// lookup is repeated only after the next block, NL-414).
    /// </summary>
    bool IsPending(ShortChannelId shortChannelId);
}