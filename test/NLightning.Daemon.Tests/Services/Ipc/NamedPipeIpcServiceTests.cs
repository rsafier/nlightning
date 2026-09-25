using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Services.Ipc;

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

    private NamedPipeIpcService CreateService()
    {
        return new NamedPipeIpcService(new Mock<IIpcAuthenticator>().Object, _configPath,
                                       new Mock<IIpcFraming>().Object, NullLogger<NamedPipeIpcService>.Instance,
                                       new Mock<IIpcRequestRouter>().Object);
    }
}