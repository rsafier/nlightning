namespace NLightning.Application.Gossip.Relay.Interfaces;

using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Protocol.Interfaces;

/// <summary>
/// How the relay hands one gossip message to one connection (NL-351): the peer's <c>PeerOutbox</c> when the peer
/// manager offers it (<see cref="Domain.Gossip.Interfaces.IPeerGossipOutbox"/>), else the peer service directly.
/// </summary>
public interface IGossipPeerSender
{
    /// <summary>
    /// Sends (or queues) <paramref name="message"/> for <paramref name="peer"/>'s connection.
    /// </summary>
    /// <param name="peer">The connection.</param>
    /// <param name="message">The gossip message.</param>
    /// <param name="size">Its wire size in bytes (the outbox's byte cap; 0 when unknown).</param>
    /// <returns>
    /// <see cref="GossipEnqueueResult.Full"/> when the connection's outbox is at its gossip cap (NL-360: nothing was
    /// queued; offer it again once <see cref="GetDepth"/> shows the outbox drained), <see cref="GossipEnqueueResult.Gone"/>
    /// when the connection is gone or closing: nothing more should go to it.
    /// </returns>
    /// <exception cref="Exception">A direct send failed (the connection is going away).</exception>
    ValueTask<GossipEnqueueResult> SendAsync(GossipPeer peer, IMessage message, int size);

    /// <summary>
    /// The gossip share of <paramref name="peer"/>'s outbox (NL-360), or null without an outbox (the direct path) or
    /// when the connection is no longer the peer's current one.
    /// </summary>
    GossipOutboxDepth? GetDepth(GossipPeer peer);
}