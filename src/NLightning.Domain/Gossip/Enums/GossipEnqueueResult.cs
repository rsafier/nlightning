namespace NLightning.Domain.Gossip.Enums;

/// <summary>
/// What happened to a gossip message handed to a peer connection's outbox (NL-360).
/// </summary>
public enum GossipEnqueueResult : byte
{
    /// <summary>Queued (or, without an outbox, sent).</summary>
    Queued = 0,

    /// <summary>
    /// Not queued: the connection's outbox already holds its gossip cap (a peer that reads slowly). The connection is
    /// still up: offer the message again once the outbox drained.
    /// </summary>
    Full = 1,

    /// <summary>
    /// Not queued: the connection is no longer the peer's current one or is closing. Nothing more should go to it.
    /// </summary>
    Gone = 2
}