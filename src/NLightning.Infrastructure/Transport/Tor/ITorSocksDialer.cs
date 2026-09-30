using System.Net.Sockets;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// Opens TCP connections through Tor's SOCKS5 port (<c>Node:Tor:SocksProxy</c>).
/// </summary>
public interface ITorSocksDialer
{
    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/> through Tor. The host (a <c>.onion</c> name, a DNS
    /// name or an IP address) is resolved by Tor, never locally. With <c>Node:Tor:StreamIsolation</c> every call gets
    /// its own circuit.
    /// </summary>
    /// <returns>A client whose stream is the tunnel to the target; the caller owns it.</returns>
    /// <exception cref="Socks5Exception">Tor refused or could not make the connection.</exception>
    /// <exception cref="Domain.Exceptions.ConnectionException">Tor's SOCKS5 port could not be reached.</exception>
    Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}