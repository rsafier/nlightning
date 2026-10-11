using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Exceptions;
using Domain.Node.Options;

/// <summary>
/// <see cref="ITorSocksDialer"/> over <c>Node:Tor:SocksProxy</c>: a TCP (or Unix socket) connection to Tor's SOCKS5
/// port, then a SOCKS5 <c>CONNECT</c> by name. Stream isolation sends fresh random credentials with every request.
/// </summary>
public sealed class TorSocksDialer : ITorSocksDialer
{
    private readonly ILogger<TorSocksDialer> _logger;
    private readonly TorOptions _torOptions;

    public TorSocksDialer(ILogger<TorSocksDialer> logger, IOptions<NodeOptions> nodeOptions)
    {
        _logger = logger;
        _torOptions = nodeOptions.Value.Tor;
    }

    /// <inheritdoc />
    public async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (!TorOptions.TryParseEndPoint(_torOptions.SocksProxy, out var proxy))
            throw new ConnectionException($"Node:Tor:SocksProxy '{_torOptions.SocksProxy}' is not a valid endpoint");

        TcpClient client;
        try
        {
            client = await ConnectToEndPointAsync(proxy, cancellationToken);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new ConnectionException($"Could not reach Tor's SOCKS5 port {_torOptions.SocksProxy} (is Tor running?)",
                                          e);
        }

        try
        {
            var credentials = _torOptions.StreamIsolation ? NewIsolationCredentials() : ((string, string)?)null;
            await Socks5Client.ConnectAsync(client.GetStream(), host, port, credentials, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Connected to {Host}:{Port} through Tor", host, port);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A TCP client connected to <paramref name="endPoint"/>: an IP or DNS endpoint, or a Unix domain socket wrapped in
    /// a <see cref="TcpClient"/> (its stream is a plain <see cref="NetworkStream"/>). It is a <see cref="TorTcpClient"/>,
    /// so a peer connection made over it gets the Tor network timeout (NL-590). TCP clients have Nagle off, as the
    /// direct peer connections (<c>TcpService</c>).
    /// </summary>
    internal static async Task<TcpClient> ConnectToEndPointAsync(EndPoint endPoint, CancellationToken cancellationToken)
    {
        switch (endPoint)
        {
            case UnixDomainSocketEndPoint unix:
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(unix, cancellationToken);
                        return new TorTcpClient { Client = socket };
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            case IPEndPoint ip:
                {
                    var client = new TorTcpClient(ip.AddressFamily) { NoDelay = true };
                    try
                    {
                        await client.ConnectAsync(ip, cancellationToken);
                        return client;
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                }
            case DnsEndPoint dns:
                {
                    var client = new TorTcpClient { NoDelay = true };
                    try
                    {
                        await client.ConnectAsync(dns.Host, dns.Port, cancellationToken);
                        return client;
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                }
            default:
                throw new ArgumentException($"Unsupported endpoint {endPoint}", nameof(endPoint));
        }
    }

    /// <summary>
    /// Random SOCKS5 credentials: Tor's <c>IsolateSOCKSAuth</c> (on by default) keeps streams with different credentials
    /// on different circuits, so no two peers share one.
    /// </summary>
    private static (string, string) NewIsolationCredentials() =>
        ($"nltg-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}",
         Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
}