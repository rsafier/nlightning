using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Services.Ipc;

using Daemon.Contracts.Utilities;
using Daemon.Ipc.Interfaces;
using Daemon.Services.Ipc;
using TestCollections;

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

    private NamedPipeIpcService CreateService()
    {
        return new NamedPipeIpcService(new Mock<IIpcAuthenticator>().Object, _configPath,
                                       new Mock<IIpcFraming>().Object, NullLogger<NamedPipeIpcService>.Instance,
                                       new Mock<IIpcRequestRouter>().Object);
    }
}