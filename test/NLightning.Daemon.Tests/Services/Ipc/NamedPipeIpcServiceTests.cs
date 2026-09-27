using System.IO.Pipes;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Services.Ipc;

using Daemon.Contracts.Utilities;
using Daemon.Ipc.Interfaces;
using Daemon.Services.Ipc;
using Domain.Client.Constants;
using Domain.Client.Enums;
using TestCollections;
using Transport.Ipc;

[Collection(SerialTestCollection.Name)]
public class NamedPipeIpcServiceTests : IDisposable
{
    private readonly string _configPath;

    public NamedPipeIpcServiceTests()
    {
        // Keep the path short: it holds a Unix socket, whose path length is limited
        _configPath = Path.Combine(Path.GetTempPath(), $"nl{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(_configPath);
    }

    public void Dispose()
    {
        Directory.Delete(_configPath, true);
    }

    [Fact]
    public async Task GivenServiceNeverStarted_WhenStopAsync_ThenCompletesWithoutThrowing()
    {
        // Arrange
        var service = CreateService();

        // Act
        var exception = await Record.ExceptionAsync(() => service.StopAsync());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task GivenStartedService_WhenStopAsync_ThenListenerStops()
    {
        // Arrange
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // Act
        var stopTask = service.StopAsync();
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(10),
                                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(stopTask, completed);
    }

    [Fact]
    public async Task GivenExistingCookie_WhenStartAsync_ThenCookieIsRotated()
    {
        // Arrange
        var cookiePath = NodeUtils.GetCookieFilePath(_configPath);
        await File.WriteAllTextAsync(cookiePath, "old-cookie", TestContext.Current.CancellationToken);
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        var firstCookie = await File.ReadAllTextAsync(cookiePath, TestContext.Current.CancellationToken);
        await service.StopAsync();

        var restartedService = CreateService();
        await restartedService.StartAsync(CancellationToken.None);
        var secondCookie = await File.ReadAllTextAsync(cookiePath, TestContext.Current.CancellationToken);
        await restartedService.StopAsync();

        // Assert
        Assert.NotEqual("old-cookie", firstCookie);
        Assert.Equal(64, firstCookie.Length);
        Assert.NotEqual(firstCookie, secondCookie);
    }

    [Fact]
    public async Task GivenStartedService_WhenStartAsync_ThenCookieIsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        var cookiePath = NodeUtils.GetCookieFilePath(_configPath);
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        var mode = OperatingSystem.IsWindows() ? UnixFileMode.None : File.GetUnixFileMode(cookiePath);
        await service.StopAsync();

        // Assert
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task GivenStartedService_WhenStopAsync_ThenCookieIsDeleted()
    {
        // Arrange
        var cookiePath = NodeUtils.GetCookieFilePath(_configPath);
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // Act
        await service.StopAsync();

        // Assert
        Assert.False(File.Exists(cookiePath));
    }

    [Fact]
    public async Task Given_MoreThanTenClientsHeld_When_AnotherClientConnects_Then_ItIsStillServed()
    {
        // Arrange: every connection is held open inside the framing read, like a PayInvoice waiting for its outcome.
        // The old cap of 10 pipe instances made the 11th client wait until one of them finished.
        const int clients = 15;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var served = 0;
        var framingMock = new Mock<IIpcFraming>();
        framingMock.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                   .Returns(async (Stream _, CancellationToken _) =>
                    {
                        Interlocked.Increment(ref served);
                        await release.Task;
                        throw new IOException("client gone");
                    });
        var service = new NamedPipeIpcService(new Mock<IIpcAuthenticator>().Object, _configPath, framingMock.Object,
                                              NullLogger<NamedPipeIpcService>.Instance,
                                              new Mock<IIpcRequestRouter>().Object);
        await service.StartAsync(CancellationToken.None);
        var pipePath = NodeUtils.GetNamedPipeFilePath(_configPath);
        var connections = new List<NamedPipeClientStream>();

        try
        {
            // Act
            for (var i = 0; i < clients; i++)
            {
                var client = new NamedPipeClientStream(".", pipePath, PipeDirection.InOut, PipeOptions.Asynchronous);
                connections.Add(client);
                await client.ConnectAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref served) < clients && DateTime.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(clients, Volatile.Read(ref served));
        }
        finally
        {
            release.TrySetResult();
            foreach (var connection in connections)
                await connection.DisposeAsync();
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Given_AClientThatSendsNothing_When_TheReadTimeoutPasses_Then_TheConnectionIsClosed()
    {
        // Arrange: without a deadline a silent client held a pipe instance until the daemon stopped
        var service = new NamedPipeIpcService(new Mock<IIpcAuthenticator>().Object, _configPath,
                                              new LengthPrefixedIpcFraming(),
                                              NullLogger<NamedPipeIpcService>.Instance,
                                              new Mock<IIpcRequestRouter>().Object)
        {
            RequestReadTimeout = TimeSpan.FromMilliseconds(200)
        };
        await service.StartAsync(CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", NodeUtils.GetNamedPipeFilePath(_configPath),
                                                           PipeDirection.InOut,
                                                           PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            // Act
            var buffer = new byte[16];
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var read = await client.ReadAsync(buffer, readTimeout.Token);

            // Assert: closed by the server with nothing written
            Assert.Equal(0, read);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Given_AnUnreadableRequest_When_Handled_Then_TheErrorDoesNotCarryTheExceptionMessage()
    {
        // Arrange
        var written = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var framingMock = new Mock<IIpcFraming>();
        framingMock.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("internal detail /home/node/.nltg"));
        framingMock.Setup(x => x.WriteAsync(It.IsAny<Stream>(), It.IsAny<IpcEnvelope>(),
                                            It.IsAny<CancellationToken>()))
                   .Callback((Stream _, IpcEnvelope envelope, CancellationToken _) => written.TrySetResult(envelope))
                   .Returns(Task.CompletedTask);
        var service = new NamedPipeIpcService(new Mock<IIpcAuthenticator>().Object, _configPath, framingMock.Object,
                                              NullLogger<NamedPipeIpcService>.Instance,
                                              new Mock<IIpcRequestRouter>().Object);
        await service.StartAsync(CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", NodeUtils.GetNamedPipeFilePath(_configPath),
                                                           PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            // Act
            await client.ConnectAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var envelope = await written.Task.WaitAsync(TimeSpan.FromSeconds(10),
                                                        TestContext.Current.CancellationToken);

            // Assert
            var error = MessagePackSerializer.Deserialize<IpcError>(envelope.Payload,
                                                                    cancellationToken: TestContext.Current
                                                                       .CancellationToken);
            Assert.Equal(IpcEnvelopeKind.Error, envelope.Kind);
            Assert.Equal(ErrorCodes.ServerError, error.Code);
            Assert.DoesNotContain("internal detail", error.Message);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Given_StartedService_When_Listening_Then_TheUnixSocketIsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets only");

        // Arrange
        var service = CreateService();
        var pipePath = NodeUtils.GetNamedPipeFilePath(_configPath);

        // Act
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(pipePath) && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            // Assert: PipeOptions.CurrentUserOnly, so other local users cannot connect
            var mode = OperatingSystem.IsWindows() ? UnixFileMode.None : File.GetUnixFileMode(pipePath);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                                           | UnixFileMode.OtherRead
                                                                           | UnixFileMode.OtherWrite));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Given_AnEnvelope_When_WrittenAndReadByTheFraming_Then_RoundTrips()
    {
        // Arrange: the server reads with MessagePack's untrusted-data security
        var framing = new LengthPrefixedIpcFraming();
        var envelope = new IpcEnvelope
        {
            Command = ClientCommand.NodeInfo,
            AuthToken = "token",
            Payload = [1, 2, 3],
            Kind = IpcEnvelopeKind.Request
        };
        using var stream = new MemoryStream();

        // Act
        await framing.WriteAsync(stream, envelope, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var read = await framing.ReadAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(envelope.CorrelationId, read.CorrelationId);
        Assert.Equal("token", read.AuthToken);
        Assert.Equal(envelope.Payload, read.Payload);
        Assert.Equal(ClientCommand.NodeInfo, read.Command);
    }

    private NamedPipeIpcService CreateService()
    {
        return new NamedPipeIpcService(new Mock<IIpcAuthenticator>().Object, _configPath,
                                       new Mock<IIpcFraming>().Object, NullLogger<NamedPipeIpcService>.Instance,
                                       new Mock<IIpcRequestRouter>().Object);
    }
}