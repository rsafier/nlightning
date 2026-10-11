using System.Net.Sockets;

namespace NLightning.Infrastructure.Transport.Events;

using Domain.Transport;

public class NewPeerConnectedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the host address of the connected peer.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the port number of the connected peer.
    /// </summary>
    public uint Port { get; }

    /// <summary>
    /// Gets the connection: one our listener accepted, or a stream the host handed to the node
    /// (<see cref="Interfaces.IPeerInboundAcceptor"/>).
    /// </summary>
    public PeerConnection Connection { get; }

    public NewPeerConnectedEventArgs(string host, uint port, PeerConnection connection)
    {
        Host = host;
        Port = port;
        Connection = connection;
    }

    public NewPeerConnectedEventArgs(string host, uint port, TcpClient tcpClient)
        : this(host, port, PeerConnection.FromTcpClient(tcpClient))
    {
    }
}