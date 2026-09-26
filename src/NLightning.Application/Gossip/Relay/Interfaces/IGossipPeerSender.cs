namespace NLightning.Application.Gossip.Relay.Interfaces;

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
    /// <returns>False when the connection is gone or closing: nothing more should go to it.</returns>
    /// <exception cref="Exception">A direct send failed (the connection is going away).</exception>
    ValueTask<bool> SendAsync(GossipPeer peer, IMessage message);
}