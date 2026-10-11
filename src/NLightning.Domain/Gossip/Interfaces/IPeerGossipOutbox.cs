namespace NLightning.Domain.Gossip.Interfaces;

using Enums;
using Models;
using Node.Interfaces;
using Protocol.Interfaces;

/// <summary>
/// The ordered send path of a peer's connection for BOLT 7 gossip (NL-351): the peer manager's per-connection
/// outbox, so our own and relayed gossip goes out in FIFO order with the channel messages queued before it instead of
/// going to the transport directly. The gossip share of each outbox is bounded (NL-360): a full outbox refuses more
/// gossip with <see cref="GossipEnqueueResult.Full"/>, distinct from a gone connection, so the caller can wait for it
/// to drain (<see cref="GetGossipDepth"/>) instead of dropping what it still has to send.
/// </summary>
public interface IPeerGossipOutbox
{
    /// <summary>
    /// Queues <paramref name="message"/> on the outbox of <paramref name="connection"/>, unless its gossip share is at
    /// the cap. Never blocks and never throws.
    /// </summary>
    /// <param name="connection">The connection the message is meant for.</param>
    /// <param name="message">A gossip message.</param>
    /// <param name="size">Its wire size in bytes (counted against the byte cap; 0 when unknown).</param>
    /// <returns>
    /// <see cref="GossipEnqueueResult.Full"/> when the outbox holds its gossip cap (nothing was queued, the connection
    /// is up), <see cref="GossipEnqueueResult.Gone"/> when <paramref name="connection"/> is no longer the peer's
    /// current connection or its outbox is closed (the peer is disconnecting).
    /// </returns>
    GossipEnqueueResult EnqueueGossip(IPeerService connection, IMessage message, int size);

    /// <summary>
    /// The gossip share of <paramref name="connection"/>'s outbox, or null when it is no longer the peer's current
    /// connection.
    /// </summary>
    GossipOutboxDepth? GetGossipDepth(IPeerService connection);

    /// <summary>
    /// Queues <paramref name="message"/> (size unknown); true only when it was queued.
    /// </summary>
    bool TryEnqueueGossip(IPeerService connection, IMessage message) =>
        EnqueueGossip(connection, message, 0) == GossipEnqueueResult.Queued;
}