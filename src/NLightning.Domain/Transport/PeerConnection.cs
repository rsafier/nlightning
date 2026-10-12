using System.Net;
using System.Net.Sockets;

namespace NLightning.Domain.Transport;

/// <summary>
/// A connected byte stream to a peer, on which BOLT 8 runs: a TCP connection the node dialed or accepted itself, or a
/// stream an embedding host handed over (an <c>IPeerConnectionDialer</c>'s or one given to the
/// <c>IPeerInboundAcceptor</c>, for example through a proxy). The connection owns what it wraps: disposing it closes
/// the TCP client or the stream.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    private readonly Stream? _stream;
    private readonly EndPoint? _remoteEndPoint;
    private int _closed;

    private PeerConnection(TcpClient? tcpClient, Stream? stream, EndPoint? remoteEndPoint, bool isProxied)
    {
        TcpClient = tcpClient;
        _stream = stream;
        _remoteEndPoint = remoteEndPoint;
        IsProxied = isProxied;
    }

    /// <summary>The TCP client of a connection the node made or accepted itself; null for a host's stream.</summary>
    public TcpClient? TcpClient { get; }

    /// <summary>
    /// True when the remote endpoint is not where the peer can be dialed: the connection came through a proxy, so the
    /// peer's stored address must not be replaced by it (an inbound peer is saved as inbound-only, NL-497) and our
    /// <c>init</c> carries no <c>remote_addr</c>.
    /// </summary>
    public bool IsProxied { get; }

    /// <summary>
    /// The remote endpoint: the socket's for a TCP connection, the one the host gave for a stream (may be null).
    /// </summary>
    public EndPoint? RemoteEndPoint
    {
        get
        {
            if (TcpClient is null)
                return _remoteEndPoint;

            try
            {
                return TcpClient.Client?.RemoteEndPoint;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// True while the connection is open: the TCP client's <see cref="System.Net.Sockets.TcpClient.Connected"/>, or,
    /// for a stream, until it is closed here (a stream the other end closed ends the next read instead).
    /// </summary>
    public bool IsConnected => TcpClient?.Connected ?? Volatile.Read(ref _closed) == 0;

    /// <summary>Wraps a TCP connection the node made or accepted itself.</summary>
    public static PeerConnection FromTcpClient(TcpClient tcpClient)
    {
        ArgumentNullException.ThrowIfNull(tcpClient);
        return new PeerConnection(tcpClient, null, null, false);
    }

    /// <summary>Wraps a connected duplex stream a host handed over.</summary>
    /// <param name="stream">The stream; readable and writable, owned by the connection from now on.</param>
    /// <param name="remoteEndPoint">The remote endpoint the node records and logs, or null.</param>
    /// <param name="isProxied">See <see cref="IsProxied"/>.</param>
    public static PeerConnection FromStream(Stream stream, EndPoint? remoteEndPoint, bool isProxied)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("A peer connection's stream must be readable and writable", nameof(stream));

        return new PeerConnection(null, stream, remoteEndPoint, isProxied);
    }

    /// <summary>The connection's byte stream.</summary>
    /// <exception cref="InvalidOperationException">The TCP client is not connected.</exception>
    /// <exception cref="ObjectDisposedException">The connection was closed.</exception>
    public Stream GetStream()
    {
        if (TcpClient is not null)
            return TcpClient.GetStream();

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) == 1, this);
        return _stream!;
    }

    /// <summary>
    /// Probes whether the connection is still open: for a TCP client a socket poll (a readable socket with no data is
    /// a closed one), for a stream <see cref="IsConnected"/>.
    /// </summary>
    public bool IsAlive()
    {
        if (TcpClient is null)
            return IsConnected;

        try
        {
            var socket = TcpClient.Client;
            return socket.Connected && (!socket.Poll(1, SelectMode.SelectRead) || socket.Available != 0);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Closes the connection; safe to call more than once.</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
            return;

        if (TcpClient is not null)
            TcpClient.Dispose();
        else
            _stream!.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => RemoteEndPoint?.ToString() ?? "unknown";
}