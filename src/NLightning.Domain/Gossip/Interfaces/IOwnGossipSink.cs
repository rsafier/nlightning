namespace NLightning.Domain.Gossip.Interfaces;

using Money;
using Protocol.Payloads;

/// <summary>
/// Receives the gossip the node generates itself for its public channels (BOLT 7 plan §3.2 step 5): the assembled
/// <c>channel_announcement</c>, our public <c>channel_update</c>s and our <c>node_announcement</c>. The graph store
/// implements it (G2-T4) to add our channels without a chain lookup; the default registration ignores everything.
/// </summary>
/// <remarks>
/// Callers may hold a channel's lock, so implementations only record or enqueue and never block. Every call can be
/// repeated with the same message (after a restart, a reconnection or a retransmission): implementations must be
/// idempotent and keep the newer of two messages for the same channel direction or node.
/// </remarks>
public interface IOwnGossipSink
{
    /// <summary>
    /// A <c>channel_announcement</c> of one of our channels with all four signatures (verified), and the channel's
    /// capacity (the funding amount; our own funding output needs no chain lookup).
    /// </summary>
    void AddOwnChannelAnnouncement(ChannelAnnouncementPayload announcement, LightningMoney capacity);

    /// <summary>Our signed <c>channel_update</c> for one of our announced channels (<c>dont_forward</c> clear).</summary>
    void AddOwnChannelUpdate(ChannelUpdatePayload update);

    /// <summary>Our signed <c>node_announcement</c> (only sent once we have an announced channel).</summary>
    void AddOwnNodeAnnouncement(NodeAnnouncementPayload announcement);
}