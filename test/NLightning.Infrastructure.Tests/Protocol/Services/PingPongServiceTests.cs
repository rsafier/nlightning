using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Protocol.Services;

using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Protocol.Services;

public class PingPongServiceTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(5);

    private readonly Mock<IMessageFactory> _messageFactoryMock = new();

    public PingPongServiceTests()
    {
        _messageFactoryMock.Setup(x => x.CreatePingMessage()).Returns(() => new PingMessage());
    }

    private PingPongService CreateService(TimeSpan networkTimeout)
    {
        return new PingPongService(_messageFactoryMock.Object,
                                   Options.Create(new NodeOptions { NetworkTimeout = networkTimeout }));
    }

    [Fact]
    public async Task Given_NoPong_When_NetworkTimeoutElapses_Then_DisconnectEventIsRaisedAndLoopStops()
    {
        // Arrange
        var service = CreateService(TimeSpan.FromMilliseconds(50));
        var disconnectTcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DisconnectEvent += (_, e) => disconnectTcs.TrySetResult(e);
        using var cts = new CancellationTokenSource();

        // Act
        var pingTask = service.StartPingAsync(cts.Token);
        var completed = await Task.WhenAny(disconnectTcs.Task, Task.Delay(s_testTimeout,
                                                                          TestContext.Current.CancellationToken));

        // Assert
        await cts.CancelAsync();
        Assert.Same(disconnectTcs.Task, completed);
        Assert.IsType<ConnectionException>(await disconnectTcs.Task);
        Assert.Same(pingTask, await Task.WhenAny(pingTask, Task.Delay(s_testTimeout,
                                                                      TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Given_PongReceived_When_NetworkTimeoutElapses_Then_DisconnectEventIsNotRaised()
    {
        // Arrange
        var service = CreateService(TimeSpan.FromMilliseconds(50));
        var disconnectRaised = false;
        service.DisconnectEvent += (_, _) => disconnectRaised = true;
        service.OnPingMessageReady += (_, ping) =>
            service.HandlePong(new PongMessage(((PingMessage)ping).Payload.NumPongBytes));
        using var cts = new CancellationTokenSource();

        // Act
        var pingTask = service.StartPingAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Task.WhenAny(pingTask, Task.Delay(s_testTimeout, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(disconnectRaised);
        Assert.True(pingTask.IsCompleted);
    }

    [Fact]
    public async Task Given_NoPong_When_CancelledBeforeTimeout_Then_DisconnectEventIsNotRaised()
    {
        // Arrange
        var service = CreateService(TimeSpan.FromSeconds(30));
        var disconnectRaised = false;
        service.DisconnectEvent += (_, _) => disconnectRaised = true;
        using var cts = new CancellationTokenSource();

        // Act
        var pingTask = service.StartPingAsync(cts.Token);
        await cts.CancelAsync();
        await Task.WhenAny(pingTask, Task.Delay(s_testTimeout, TestContext.Current.CancellationToken));

        // Assert
        Assert.True(pingTask.IsCompleted);
        Assert.False(disconnectRaised);
    }
}