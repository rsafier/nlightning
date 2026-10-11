using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NLightning.Integration.Tests.Docker.Utils;

using Domain.Exceptions;
using Domain.Transport;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// Wraps the node's <see cref="ITcpService"/> and remembers every socket it hands out, so
/// <see cref="NLightningTestNode.CrashAsync"/> can reset them all at once: the peers see the connection die without a
/// final <c>error</c>/<c>warning</c> or a graceful close, as if the process had died.
/// </summary>
internal sealed class CrashableTcpService : ITcpService
{
    private readonly ITcpService _inner;
    private readonly ConcurrentBag<PeerConnection> _clients = [];
    private volatile bool _crashed;

    public CrashableTcpService(ITcpService inner)
    {
        _inner = inner;
        _inner.OnNewPeerConnected += HandleNewPeerConnected;
    }

    public List<EndPoint> ListeningTo => _inner.ListeningTo;

    public event EventHandler<NewPeerConnectedEventArgs>? OnNewPeerConnected;

    public Task StartListeningAsync(CancellationToken cancellationToken)
    {
        ThrowIfCrashed();
        return _inner.StartListeningAsync(cancellationToken);
    }

    public Task StopListeningAsync() => _inner.StopListeningAsync();

    public Task AcceptInboundAsync(Stream stream, EndPoint? remoteEndPoint, bool isProxied,
                                   CancellationToken cancellationToken = default)
    {
        ThrowIfCrashed();
        return _inner.AcceptInboundAsync(stream, remoteEndPoint, isProxied, cancellationToken);
    }

    public async Task<ConnectedPeer> ConnectToPeerAsync(PeerAddress peerAddress)
    {
        ThrowIfCrashed();
        var connectedPeer = await _inner.ConnectToPeerAsync(peerAddress);
        _clients.Add(connectedPeer.Connection);
        if (_crashed)
        {
            Reset(connectedPeer.Connection);
            ThrowIfCrashed();
        }

        return connectedPeer;
    }

    /// <summary>
    /// Stops listening and resets every connection. Later connection attempts fail.
    /// </summary>
    public async Task CrashAsync()
    {
        _crashed = true;
        try
        {
            await _inner.StopListeningAsync();
        }
        catch (Exception)
        {
            // Not listening
        }

        foreach (var client in _clients)
            Reset(client);
    }

    private void HandleNewPeerConnected(object? sender, NewPeerConnectedEventArgs args)
    {
        _clients.Add(args.Connection);
        if (_crashed)
        {
            Reset(args.Connection);
            return;
        }

        OnNewPeerConnected?.Invoke(this, args);
    }

    private void ThrowIfCrashed()
    {
        if (_crashed)
            throw new ConnectionException("The test node crashed");
    }

    /// <summary>
    /// Closes the socket with a zero linger time, which sends a TCP RST instead of a FIN.
    /// </summary>
    private static void Reset(PeerConnection connection)
    {
        try
        {
            if (connection.TcpClient is not { } client)
            {
                connection.Close();
                return;
            }

            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
        }
        catch (Exception)
        {
            // Already closed
        }
    }
}