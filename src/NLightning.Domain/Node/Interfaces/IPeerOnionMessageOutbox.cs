namespace NLightning.Domain.Node.Interfaces;

using Crypto.ValueObjects;
using Protocol.Messages;

/// <summary>
/// The send path of BOLT 4 <c>onion_message</c>s (wave M6): the peer manager's per-connection outbox of a connected
/// peer, where onion messages are a bounded, lower-priority class that never holds a channel message back (the
/// <see cref="Gossip.Interfaces.IPeerGossipOutbox"/> pattern).
/// </summary>
/// <remarks>
/// Never opens a connection (BOLT12 plan D6: an onion message for a peer we are not connected to is dropped) and never
/// blocks, so it is safe on a peer's read loop.
/// </remarks>
public interface IPeerOnionMessageOutbox
{
    /// <summary>
    /// Whether <paramref name="peerNodeId"/> is connected (its init exchange done) and negotiated
    /// <c>option_onion_messages</c>, so <see cref="TryEnqueueOnionMessage"/> can queue for it.
    /// </summary>
    /// <param name="peerNodeId">The peer's node id.</param>
    bool CanSendOnionMessage(CompactPubKey peerNodeId);

    /// <summary>
    /// Queues <paramref name="message"/> on the outbox of the current connection to <paramref name="peerNodeId"/>.
    /// Never blocks and never throws.
    /// </summary>
    /// <param name="peerNodeId">The peer's node id.</param>
    /// <param name="message">The onion message.</param>
    /// <returns>
    /// False when nothing was queued: the peer is not connected, it did not negotiate <c>option_onion_messages</c>,
    /// its outbox already holds its cap of onion messages (a peer that reads slowly), or it is disconnecting.
    /// </returns>
    bool TryEnqueueOnionMessage(CompactPubKey peerNodeId, OnionMessageMessage message);
}