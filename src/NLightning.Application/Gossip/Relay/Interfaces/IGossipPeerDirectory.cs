namespace NLightning.Application.Gossip.Relay.Interfaces;

using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;

/// <summary>
/// The peers gossip can go to now: every connection that finished the <c>init</c> exchange.
/// </summary>
public interface IGossipPeerDirectory
{
    /// <summary>The connected peers, each with the peer service of its current connection.</summary>
    IReadOnlyList<GossipPeer> GetConnectedPeers();
}

/// <summary>
/// One connected peer: its node id and the <see cref="IPeerService"/> of its current connection (a new connection has
/// a new service, so what was sent is tracked per service).
/// </summary>
public sealed record GossipPeer(CompactPubKey NodeId, IPeerService Service);