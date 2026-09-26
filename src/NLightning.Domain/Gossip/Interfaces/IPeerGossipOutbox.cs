namespace NLightning.Domain.Gossip.Interfaces;

using Node.Interfaces;
using Protocol.Interfaces;

/// <summary>
/// The ordered send path of a peer's connection for BOLT 7 gossip (NL-351): the peer manager's per-connection
/// outbox, so our own and relayed gossip goes out in FIFO order with the channel messages queued before it instead of
/// going to the transport directly.
/// </summary>
public interface IPeerGossipOutbox
{
    /// <summary>
    /// Queues <paramref name="message"/> on the outbox of <paramref name="connection"/>. Never blocks and never throws.
    /// </summary>
    /// <returns>
    /// False when <paramref name="connection"/> is no longer the peer's current connection or its outbox is closed
    /// (the peer is disconnecting): nothing was queued.
    /// </returns>
    bool TryEnqueueGossip(IPeerService connection, IMessage message);
}