namespace NLightning.Domain.Node.Interfaces;

using Crypto.ValueObjects;
using Models;
using ValueObjects;

/// <summary>
/// Interface for the peer manager.
/// </summary>
public interface IPeerManager
{
    /// <summary>
    /// Starts the peer manager asynchronously.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the peer manager asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task StopAsync();

    /// <summary>
    /// Connects to a peer.
    /// </summary>
    /// <param name="peerAddressInfo">The peer address to connect to.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task<PeerModel> ConnectToPeerAsync(PeerAddressInfo peerAddressInfo);

    /// <summary>
    /// Connects to a peer like <see cref="ConnectToPeerAsync"/>, giving up when <paramref name="cancellationToken"/> is cancelled before the connection is
    /// kept: the TCP connect, the BOLT 8 handshake and the init exchange are abandoned and their connection closed, so
    /// a cancelled dial never becomes a session or a saved peer. Once the session is installed the dial completes.
    /// </summary>
    /// <param name="peerAddressInfo">The peer address to connect to.</param>
    /// <param name="cancellationToken">Cancels the dial (for example a caller's timeout).</param>
    /// <returns>The connected peer.</returns>
    /// <exception cref="OperationCanceledException">The dial was cancelled before the session was installed.
    /// </exception>
    Task<PeerModel> DialPeerAsync(PeerAddressInfo peerAddressInfo, CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects a peer.
    /// </summary>
    /// <param name="compactPubKey" cref="CompactPubKey">CompactPubKey of the peer</param>
    /// <param name="exception">Optional exception that caused the disconnection</param>
    void DisconnectPeer(CompactPubKey compactPubKey, Exception? exception = null);

    List<PeerModel> ListPeers();
    PeerModel? GetPeer(CompactPubKey peerId);
}