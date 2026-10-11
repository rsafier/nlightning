using System.Net;

namespace NLightning.Infrastructure.Transport.Interfaces;

/// <summary>
/// Hands the node an inbound peer connection an embedding host accepted itself (for example on an external TCP
/// proxy). The node runs BOLT 8 as responder on it exactly as on a connection its own listener accepted
/// (<c>Node:ListenAddresses</c>; <c>Node:ListenEnabled=false</c> runs without that listener). Registered by
/// <c>AddInfrastructureServices</c> as the node's <see cref="ITcpService"/>.
/// </summary>
public interface IPeerInboundAcceptor
{
    /// <summary>
    /// Accepts <paramref name="stream"/> as a connection a peer made to us. Returns once the node took the stream;
    /// the handshake and the <c>init</c> exchange then run in the background, and a failure closes the stream.
    /// </summary>
    /// <param name="stream">A connected, readable and writable stream; the node owns it once this returns.</param>
    /// <param name="remoteEndPoint">The endpoint the node records and logs for the connection, or null.</param>
    /// <param name="isProxied">
    /// True when <paramref name="remoteEndPoint"/> is not where the peer can be dialed back (the proxy's own
    /// address, or a client address behind it): the peer is then never saved at it (a saved dialable address is kept,
    /// else the peer's announced one or none: inbound-only, NL-497) and our <c>init</c> carries no
    /// <c>remote_addr</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the hand-over.</param>
    /// <exception cref="InvalidOperationException">
    /// The node is not accepting connections (not started, or stopping); the caller keeps the stream.
    /// </exception>
    Task AcceptInboundAsync(Stream stream, EndPoint? remoteEndPoint, bool isProxied,
                            CancellationToken cancellationToken = default);
}