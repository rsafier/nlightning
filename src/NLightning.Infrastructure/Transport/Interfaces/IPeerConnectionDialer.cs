namespace NLightning.Infrastructure.Transport.Interfaces;

using Domain.Crypto.ValueObjects;
using Protocol.Models;

/// <summary>
/// An optional outbound dialer an embedding host may register (none by default), for example to reach peers through
/// an external TCP proxy. Every outbound peer connection asks it first, whatever started the dial (startup, the
/// reconnect backoff, <c>connect</c>, the bootstrap and graph dials, an open to a peer we are not connected to); the
/// node then runs BOLT 8 as initiator on the stream it returns, exactly as on a connection of its own.
/// </summary>
public interface IPeerConnectionDialer
{
    /// <summary>
    /// Connects to the peer <paramref name="nodeId"/> at <paramref name="address"/>.
    /// </summary>
    /// <param name="nodeId">The peer's node id (the BOLT 8 static key the handshake expects;
    /// <paramref name="address"/>'s <see cref="PeerAddress.PubKey"/>).</param>
    /// <param name="address">The address the node would dial.</param>
    /// <param name="cancellationToken">Cancelled after <c>Node:NetworkTimeout</c> or when the caller gives up.</param>
    /// <returns>
    /// A connected, readable and writable stream, owned by the node from then on (it closes it); or null to let the
    /// node dial the address itself (directly or through Tor, <c>Node:Tor</c>).
    /// </returns>
    /// <remarks>Any exception fails the dial like a refused TCP connection.</remarks>
    ValueTask<Stream?> DialAsync(CompactPubKey nodeId, PeerAddress address, CancellationToken cancellationToken);
}