namespace NLightning.Domain.Node.Interfaces;

using Crypto.ValueObjects;
using Transport;

/// <summary>
/// Interface for creating peer services.
/// </summary>
public interface IPeerServiceFactory
{
    /// <summary>
    /// Creates a peer we're connecting to.
    /// </summary>
    /// <param name="peerPubKey">Peer public key</param>
    /// <param name="connection">The connection we dialed</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created peer.</returns>
    Task<IPeerService> CreateConnectedPeerAsync(CompactPubKey peerPubKey, PeerConnection connection);

    /// <summary>
    /// Creates a peer connecting to us.
    /// </summary>
    /// <param name="connection">The connection the peer made (accepted by our listener or handed over by the
    /// host)</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created peer.</returns>
    Task<IPeerService> CreateConnectingPeerAsync(PeerConnection connection);
}