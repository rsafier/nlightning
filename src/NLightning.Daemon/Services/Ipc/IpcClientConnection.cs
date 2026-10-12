using System.IO.Pipes;

namespace NLightning.Daemon.Services.Ipc;

/// <summary>
/// The client connection one IPC request is being served on (NL-592). A long-running handler — <c>shutdown
/// --wait</c> — links <see cref="Disconnected"/> into its wait, so a client that goes away (Ctrl-C) ends the wait and
/// reopens the drain gate; short handlers are unaffected.
/// </summary>
/// <remarks>
/// The current connection flows to the handlers with the request's async flow
/// (<see cref="IpcClientConnectionAccessor.Current"/>). The watcher reads the stream: the client sends exactly one
/// request per connection, so a completed read (0 bytes) or a failing one means it is gone, while a pending read means
/// it is still there.
/// </remarks>
internal interface IIpcClientConnection
{
    /// <summary>Cancels when the client's connection is gone.</summary>
    CancellationToken Disconnected { get; }
}

internal sealed class IpcClientConnection : IIpcClientConnection, IDisposable
{
    public static readonly IIpcClientConnection None = new IpcClientConnection();

    private readonly NamedPipeServerStream? _stream;
    private readonly CancellationTokenSource _disconnected = new();
    private readonly CancellationToken _serverStopping;

    private IpcClientConnection()
    {
    }

    public IpcClientConnection(NamedPipeServerStream stream, CancellationToken serverStopping)
    {
        _stream = stream;
        _serverStopping = serverStopping;
    }

    /// <inheritdoc />
    public CancellationToken Disconnected => _disconnected.Token;

    /// <summary>Starts watching for the client's disappearance; fire and forget.</summary>
    public void WatchForDisconnect()
    {
        if (_stream is not { } stream)
            return;

        _ = WatchAsync(stream);
    }

    private async Task WatchAsync(NamedPipeServerStream stream)
    {
        try
        {
            var buffer = new byte[16];
            while (true)
            {
                // The client sends nothing after its request: a read that returns (0 bytes, an error or the server
                // stopping) means the connection is gone; a pending read means the client is still waiting
                var read = await stream.ReadAsync(buffer, _serverStopping);
                if (read == 0)
                    break;
            }
        }
        catch
        {
            // The stream closed or the server stopped: disconnected, or the answer was written already
        }
        finally
        {
            try { _disconnected.Cancel(); }
            catch
            {
                // Disposed while the watch was still pending
            }
        }
    }

    public void Dispose() => _disconnected.Dispose();
}

/// <summary>
/// Hands the connection a request is being served on to the handlers that need it
/// (an <see cref="Microsoft.Extensions.DependencyInjection">HttpContextAccessor</see>-like ambient value, NL-592).
/// </summary>
internal sealed class IpcClientConnectionAccessor
{
    private static readonly AsyncLocal<IIpcClientConnection?> s_current = new();

    /// <summary>The connection of the request being served, null outside one.</summary>
    public IIpcClientConnection? Current
    {
        get => s_current.Value ?? IpcClientConnection.None;
        set => s_current.Value = ReferenceEquals(value, IpcClientConnection.None) ? null : value;
    }
}