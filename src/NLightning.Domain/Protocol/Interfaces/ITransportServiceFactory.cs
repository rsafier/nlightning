using System.Net.Sockets;

namespace NLightning.Domain.Protocol.Interfaces;

using Transport;

/// <summary>
/// Interface for a transport service factory.
/// </summary>
/// <remarks>
/// This interface is used to create a transport service in test environments.
/// </remarks>
public interface ITransportServiceFactory
{
    /// <summary>
    /// Creates a transport service.
    /// </summary>
    ITransportService CreateTransportService(bool isInitiator, ReadOnlySpan<byte> s, ReadOnlySpan<byte> rs,
                                             TcpClient tcpClient);

    /// <summary>
    /// Creates a transport service whose local static ECDH is computed by
    /// <paramref name="protectedStaticEcdh"/>, so the static private key never leaves its key manager (NL-436).
    /// </summary>
    /// <param name="isInitiator">Whether we are the handshake initiator.</param>
    /// <param name="localStaticPublicKey">Our local static public key.</param>
    /// <param name="rs">The remote static public key (the initiator's expectation; the responder passes a
    /// placeholder, its remote static arrives in act three).</param>
    /// <param name="tcpClient">The connected TCP client.</param>
    /// <param name="protectedStaticEcdh">Computes the ECDH of the protected local static private key.</param>
    ITransportService CreateTransportService(bool isInitiator, ReadOnlySpan<byte> localStaticPublicKey,
                                             ReadOnlySpan<byte> rs, TcpClient tcpClient,
                                             ProtectedStaticEcdh protectedStaticEcdh);
}