using System.Net.Sockets;

namespace NLightning.Infrastructure.Node.ValueObjects;

using Domain.Crypto.ValueObjects;
using Domain.Transport;

public readonly record struct ConnectedPeer
{
    /// <summary>
    /// The public key of the peer we are connected to.
    /// </summary>
    public CompactPubKey CompactPubKey { get; }

    /// <summary>
    /// The host address of the connected peer.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// The port used by the peer to establish the connection.
    /// </summary>
    public uint Port { get; }

    /// <summary>
    /// The connection to the peer: a TCP client, or the stream an <c>IPeerConnectionDialer</c> returned.
    /// </summary>
    public PeerConnection Connection { get; }

    /// <summary>
    /// Represents a connected peer in the network, consisting of a compact public key and its connection.
    /// </summary>
    /// <param name="compactPubKey">The compact public key of the peer.</param>
    /// <param name="host">The host address of the connected peer.</param>
    /// <param name="port">The port used by the peer to establish the connection.</param>
    /// <param name="connection">The connection to the peer.</param>
    public ConnectedPeer(CompactPubKey compactPubKey, string host, uint port, PeerConnection connection)
    {
        CompactPubKey = compactPubKey;
        Host = host;
        Port = port;
        Connection = connection;
    }

    /// <summary>
    /// A connected peer on a TCP connection of our own.
    /// </summary>
    public ConnectedPeer(CompactPubKey compactPubKey, string host, uint port, TcpClient tcpClient)
        : this(compactPubKey, host, port, PeerConnection.FromTcpClient(tcpClient))
    {
    }
}