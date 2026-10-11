using System.Net;
using System.Net.Sockets;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Node.Options;
using Domain.Transport;

/// <summary>
/// A <see cref="TcpClient"/> connected to Tor (its SOCKS5 or control port): a peer connection over it is routed
/// through Tor and gets <see cref="TorOptions.GetNetworkTimeout"/> (NL-590).
/// </summary>
internal sealed class TorTcpClient : TcpClient
{
    public TorTcpClient() { }

    public TorTcpClient(AddressFamily family) : base(family) { }

    /// <summary>
    /// The network timeout of a peer connection: the Tor one for a connection dialed through Tor, or an inbound one
    /// from loopback while our onion service is on (Tor delivers the service's connections from loopback; a local
    /// peer gets the longer timeout too), else <c>Node:NetworkTimeout</c> (also for a stream a host handed over).
    /// </summary>
    public static TimeSpan GetNetworkTimeout(NodeOptions nodeOptions, PeerConnection connection, bool inbound)
    {
        var tor = nodeOptions.Tor;
        if (!tor.IsEnabled || connection.TcpClient is not { } tcpClient)
            return nodeOptions.NetworkTimeout;

        var throughTor = tcpClient is TorTcpClient
                      || (inbound && tor.IsOnionServiceEnabled && IsFromLoopback(tcpClient));
        return throughTor ? tor.GetNetworkTimeout(nodeOptions.NetworkTimeout) : nodeOptions.NetworkTimeout;
    }

    private static bool IsFromLoopback(TcpClient tcpClient)
    {
        try
        {
            return tcpClient.Client?.RemoteEndPoint is IPEndPoint endPoint
                && IPAddress.IsLoopback(endPoint.Address.IsIPv4MappedToIPv6
                                            ? endPoint.Address.MapToIPv4()
                                            : endPoint.Address);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }
}