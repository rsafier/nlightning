using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NLightning.Testing.Cluster.Reach;

/// <summary>
/// A tiny TCP listener in the test process, standing in for the in-process NLightning node that LND or CLN dial back
/// to: it answers every connection with <see cref="Banner"/> and records the remote endpoint it saw, so a probe from a
/// pod proves the connection reached this process and shows the address our node would see for the peer.
/// </summary>
public sealed class HostListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<IPEndPoint> _accepted = new();
    private readonly Task _acceptLoop;

    private HostListener(TcpListener listener, string banner)
    {
        _listener = listener;
        Banner = banner;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>The line every connection gets (ends with a newline on the wire).</summary>
    public string Banner { get; }

    /// <summary>The address and (ephemeral) port the listener is bound to.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public int Port => LocalEndPoint.Port;

    /// <summary>The remote endpoints of the connections accepted so far, in order.</summary>
    public IReadOnlyCollection<IPEndPoint> Accepted => _accepted.ToArray();

    /// <summary>
    /// Starts a listener on <paramref name="address"/> (default <see cref="IPAddress.Loopback"/>: OrbStack forwards
    /// <c>host.orb.internal</c> to the Mac's loopback) at <paramref name="port"/> (0: an ephemeral port).
    /// </summary>
    public static HostListener Start(IPAddress? address = null, int port = 0, string? banner = null)
    {
        var listener = new TcpListener(address ?? IPAddress.Loopback, port);
        listener.Start();
        return new HostListener(listener, banner ?? $"nltg-host-{Guid.NewGuid():N}");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var banner = Encoding.UTF8.GetBytes(Banner + "\n");
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            using (socket)
            {
                if (socket.RemoteEndPoint is IPEndPoint remote)
                    _accepted.Enqueue(remote);
                try
                {
                    await socket.SendAsync(banner, SocketFlags.None, _stop.Token).ConfigureAwait(false);
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    // The prober went away; the accept is recorded
                }
            }
        }
    }
}