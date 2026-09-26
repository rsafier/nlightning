namespace NLightning.Application.Gossip.Relay.Interfaces;

using Domain.Protocol.Payloads;

/// <summary>
/// Sends gossip to the connected peers at a periodic flush (BOLT 7: SHOULD flush outgoing gossip every 60 seconds).
/// This is the minimal own-message path of the BOLT 7 plan (G1-T7): our own <c>channel_announcement</c>s,
/// <c>channel_update</c>s and <c>node_announcement</c> go to every connected peer whose <c>init</c> networks include
/// our chain, regardless of any <c>gossip_timestamp_filter</c> (B7-Q-05: SHOULD send our own gossip), each once per
/// connection; within a flush all <c>channel_announcement</c>s go first, then the <c>channel_update</c>s, then the
/// <c>node_announcement</c>. Relaying other nodes' gossip (filters, origin suppression, backlog) is G3-T3.
/// </summary>
/// <remarks>
/// The enqueue methods only record the message (callers may hold a channel lock). A newer message replaces the
/// older one for the same channel, channel direction or node. A <c>channel_announcement</c> goes out only once an
/// update for its channel is queued too (BOLT 7: MUST NOT send a <c>channel_announcement</c> without a
/// <c>channel_update</c>).
/// </remarks>
public interface IGossipRelayScheduler
{
    /// <summary>Queues our assembled <c>channel_announcement</c>.</summary>
    void EnqueueOwnChannelAnnouncement(ChannelAnnouncementPayload announcement);

    /// <summary>Queues our <c>channel_update</c> for an announced channel (<c>dont_forward</c> clear).</summary>
    void EnqueueOwnChannelUpdate(ChannelUpdatePayload update);

    /// <summary>Queues our <c>node_announcement</c>.</summary>
    void EnqueueOwnNodeAnnouncement(NodeAnnouncementPayload announcement);

    /// <summary>
    /// Sends every queued message that the current connection of each connected peer has not received yet, in the
    /// order above. The periodic flush calls it; a caller may call it to flush at once.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}