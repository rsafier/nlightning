using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Transport.Services;

using Domain.Exceptions;
using Domain.Node.Constants;
using Domain.Node.Options;
using Events;
using Interfaces;
using Node.ValueObjects;
using Protocol.Models;
using Tor;

public class TcpService : ITcpService
{
    /// <summary>Idle time before the first TCP keepalive probe on a peer socket: 60 s (NL-806).</summary>
    internal const int KeepAliveTimeSeconds = 60;

    /// <summary>Time between two unanswered TCP keepalive probes: 10 s.</summary>
    internal const int KeepAliveIntervalSeconds = 10;

    /// <summary>Unanswered probes before the kernel closes the socket: 5 (about 110 s of silence in all).</summary>
    internal const int KeepAliveRetryCount = 5;

    private readonly ILogger<TcpService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly ITorSocksDialer? _torSocksDialer;
    private readonly List<TcpListener> _listeners = [];

    private CancellationTokenSource? _cts;
    private Task? _listeningTask;

    public List<EndPoint> ListeningTo => _listeners.Select(l => l.LocalEndpoint).ToList();

    /// <inheritdoc />
    public event EventHandler<NewPeerConnectedEventArgs>? OnNewPeerConnected;

    public TcpService(ILogger<TcpService> logger, IOptions<NodeOptions> nodeOptions,
                      ITorSocksDialer? torSocksDialer = null)
    {
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _torSocksDialer = torSocksDialer;
    }

    /// <inheritdoc />
    public Task StartListeningAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var address in _nodeOptions.ListenAddresses)
        {
            if (!TryParseListenAddress(address, out var endPoint))
            {
                _logger.LogWarning("Invalid listen address: {Address}", address);
                continue;
            }

            var listener = new TcpListener(endPoint.Address, endPoint.Port);
            if (endPoint.Address.AddressFamily == AddressFamily.InterNetworkV6
             && endPoint.Address.Equals(IPAddress.IPv6Any))
            {
                // The wildcard :: is bound dual-stack (IPV6_V6ONLY off), so IPv4 peers reach us on it too; a specific
                // IPv6 address (e.g. ::1) keeps an IPv6-only socket, where dual mode is not allowed (NL-107).
                listener.Server.DualMode = true;
            }

            listener.Start();
            _listeners.Add(listener);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Listening for connections on {Address}:{Port}", endPoint.Address,
                                       endPoint.Port);
        }

        _listeningTask = ListenForConnectionsAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopListeningAsync()
    {
        if (_cts is null)
            throw new InvalidOperationException("Service is not running");

        await _cts.CancelAsync();

        foreach (var listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping listener");
            }
        }

        _listeners.Clear();

        if (_listeningTask is not null)
        {
            try
            {
                await _listeningTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Node:Tor</c> decides the route: an onion service goes through Tor's SOCKS5 port (and cannot be dialed with Tor
    /// off), and in Tor-only mode every address does, host names resolved by Tor, except loopback and private-network IP
    /// addresses, which Tor refuses and which are dialed directly (NL-588); otherwise the connection is direct, a host
    /// name resolved locally.
    /// </remarks>
    /// <exception cref="ConnectionException">Thrown when the connection to the peer fails.</exception>
    public async Task<ConnectedPeer> ConnectToPeerAsync(PeerAddress peerAddress)
    {
        var tor = _nodeOptions.Tor;
        if (!tor.CanDial(peerAddress.Type))
            throw new ConnectionException(peerAddress.IsOnion
                                              ? $"Cannot connect to onion service {peerAddress.Host}: Tor is off (set "
                                              + "Node:Tor:Mode to Hybrid or TorOnly and run Tor)"
                                              : $"Cannot connect to {peerAddress.Host}: unsupported address type");

        if (tor.UsesProxy(peerAddress.Type, peerAddress.IpAddress))
            return await ConnectThroughTorAsync(peerAddress, tor.ConnectTimeout);

        // Nagle off: a peer message is often followed at once by another (revoke_and_ack then commitment_signed or
        // stfu), and Nagle would hold the second until the peer's delayed ACK of the first (about 40 ms)
        var tcpClient = new TcpClient { NoDelay = true };
        EnableKeepAlive(tcpClient.Client, _logger);
        try
        {
            using var timeout = new CancellationTokenSource(_nodeOptions.NetworkTimeout);
            if (peerAddress.IpAddress is { } ip)
                await tcpClient.ConnectAsync(ip, peerAddress.Port, timeout.Token);
            else
                await tcpClient.ConnectAsync(peerAddress.Host, peerAddress.Port, timeout.Token);

            return new ConnectedPeer(peerAddress.PubKey, peerAddress.Host, (uint)peerAddress.Port, tcpClient);
        }
        catch (OperationCanceledException)
        {
            tcpClient.Dispose();
            throw new ConnectionException($"Timeout connecting to peer {peerAddress.Host}:{peerAddress.Port}");
        }
        catch (Exception e)
        {
            tcpClient.Dispose();
            throw new ConnectionException($"Failed to connect to peer {peerAddress.Host}:{peerAddress.Port}", e);
        }
    }

    /// <summary>
    /// Turns on TCP keepalive on a direct peer socket (<see cref="KeepAliveTimeSeconds"/>,
    /// <see cref="KeepAliveIntervalSeconds"/>, <see cref="KeepAliveRetryCount"/>), a second guard behind the
    /// <c>ping</c> keep-alive (<c>Node:PingInterval</c>) against a connection that died silently (NL-806). A
    /// connection through Tor is not covered: its socket ends at the local Tor proxy. A platform that refuses one of
    /// the options keeps the others and the connection (logged at debug).
    /// </summary>
    internal static void EnableKeepAlive(Socket socket, ILogger logger)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval,
                                   KeepAliveIntervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount,
                                   KeepAliveRetryCount);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException)
        {
            logger.LogDebug(e, "TCP keepalive could not be fully set on a peer socket; the ping keep-alive remains");
        }
    }

    private async Task<ConnectedPeer> ConnectThroughTorAsync(PeerAddress peerAddress, TimeSpan connectTimeout)
    {
        if (_torSocksDialer is null)
            throw new ConnectionException($"Cannot connect to {peerAddress.Host} through Tor: no Tor dialer registered");

        try
        {
            using var timeout = new CancellationTokenSource(connectTimeout);
            var tcpClient = await _torSocksDialer.ConnectAsync(peerAddress.Host, peerAddress.Port, timeout.Token);
            return new ConnectedPeer(peerAddress.PubKey, peerAddress.Host, (uint)peerAddress.Port, tcpClient);
        }
        catch (OperationCanceledException)
        {
            throw new ConnectionException($"Timeout connecting to peer {peerAddress.Host}:{peerAddress.Port} through "
                                        + $"Tor ({connectTimeout})");
        }
        catch (ConnectionException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ConnectionException($"Failed to connect to peer {peerAddress.Host}:{peerAddress.Port} through Tor",
                                          e);
        }
    }

    /// <summary>
    /// Reads a <c>Node:ListenAddresses</c> entry: <c>[ipv6]:port</c> for IPv6 (e.g. <c>[::]:9735</c>,
    /// <c>[2001:db8::1]:9735</c>), <c>ip:port</c> for IPv4, or a bare IP address, which takes the BOLT 7 default port
    /// (NL-107). A host name is not a listen address: the listener binds an IP endpoint only.
    /// </summary>
    internal static bool TryParseListenAddress(string? address, out IPEndPoint endPoint)
    {
        var text = address?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // A leading bracket decides the form: a port is required. IPAddress.TryParse accepts bracketed forms too
            // (.NET 10) and silently drops the port, so the bracket is checked before any bare-address handling.
            if (text.StartsWith('['))
            {
                if (IPEndPoint.TryParse(text, out var bracketed) && bracketed.Port != 0)
                {
                    endPoint = bracketed;
                    return true;
                }
            }
            else if (IPAddress.TryParse(text, out var ipAddress))
            {
                // A bare address ("::", "0.0.0.0", "2001:db8::1", also an unbracketed IPv6 address such as
                // "::1:9735", which is the IPv6 address ::0.1.151.53) takes the default port.
                endPoint = new IPEndPoint(ipAddress, (int)NodeConstants.DefaultPort);
                return true;
            }
            else if (IPEndPoint.TryParse(text, out var parsed) && parsed.Port != 0)
            {
                // Unbracketed "ip:port" (IPv4); port 0 (an ephemeral port nobody could dial us on) is rejected.
                endPoint = parsed;
                return true;
            }
        }

        endPoint = new IPEndPoint(IPAddress.None, 0);
        return false;
    }

    private async Task ListenForConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var listener in _listeners)
                {
                    if (!listener.Pending())
                        continue;

                    var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                    try
                    {
                        tcpClient.NoDelay = true; // as on our outbound connections
                        EnableKeepAlive(tcpClient.Client, _logger);
                    }
                    catch (SocketException e)
                    {
                        // The peer is already gone (macOS answers EINVAL for a reset connection); never let one
                        // connection end the listener
                        _logger.LogDebug(e, "Dropping a connection closed before it was set up");
                        tcpClient.Dispose();
                        continue;
                    }

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            _logger.LogInformation("New peer connection from {RemoteEndPoint}",
                                                   tcpClient.Client.RemoteEndPoint);

                            if (tcpClient.Client.RemoteEndPoint is not IPEndPoint ipEndPoint)
                            {
                                _logger.LogError("Failed to get remote endpoint for {RemoteEndPoint}",
                                                 tcpClient.Client.RemoteEndPoint);
                                return;
                            }

                            // Raise the event for a new peer connection
                            OnNewPeerConnected?.Invoke(
                                this,
                                new NewPeerConnectedEventArgs(ipEndPoint.Address.ToString(), (uint)ipEndPoint.Port,
                                                              tcpClient));
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Error accepting peer connection for {RemoteEndPoint}",
                                             tcpClient.Client.RemoteEndPoint);
                        }
                    }, cancellationToken);
                }

                await Task.Delay(100, cancellationToken); // Avoid busy-waiting
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stopping listener service");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unhandled exception in listener service");
        }
    }
}