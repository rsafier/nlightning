using System.IO.Pipes;

namespace NLightning.Daemon.Tests.Services.Ipc;

using global::NLightning.Daemon.Services.Ipc;

/// <summary>
/// The client connection the IPC server exposes to its handlers (NL-592): <see cref="IpcClientConnection.Disconnected"/>
/// cancels when the client's end of the connection goes away (a <c>shutdown --wait</c> gives up and the gate reopens).
/// </summary>
public sealed class IpcClientConnectionTests
{
    [Fact(Timeout = 10000)]
    public async Task Given_AConnectedClient_When_ItDisconnects_Then_TheWatcherCancels()
    {
        // Arrange: the same pipe shape production uses (CurrentUserOnly on both ends, an absolute path)
        var pipeName = Path.Combine(Path.GetTempPath(), $"nltg-test-{Guid.NewGuid():N}.sock");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                     PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                                                     PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var connect = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await connect;

        using var stopping = new CancellationTokenSource();
        using var connection = new IpcClientConnection(server, stopping.Token);
        Assert.False(connection.Disconnected.IsCancellationRequested);
        connection.WatchForDisconnect();

        // Act: the client (Ctrl-C) closes its end; the server's pending read returns
        client.Dispose();

        // Assert
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!connection.Disconnected.IsCancellationRequested && !timeout.IsCancellationRequested)
            await Task.Delay(10, timeout.Token);
        Assert.True(connection.Disconnected.IsCancellationRequested);
    }

    [Fact(Timeout = 10000)]
    public void Given_NoClientConnection_When_TheAccessorIsRead_Then_ItIsTheNoneConnection()
    {
        // Arrange
        var accessor = new IpcClientConnectionAccessor();

        // Act
        var current = accessor.Current;

        // Assert: the None connection's token never cancels, so a wait without a pipe does not spuriously cancel
        Assert.NotNull(current);
        Assert.False(current.Disconnected.IsCancellationRequested);
    }
}