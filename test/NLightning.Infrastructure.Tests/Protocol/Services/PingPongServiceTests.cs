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

    [Fact]
    public async Task Given_NoPingInFlight_When_PingAsync_Then_APingGoesOutAndItsPongAnswersIt()
    {
        // Arrange - NL-251: ping before commitment_signed
        var service = CreateService(TimeSpan.FromSeconds(30));
        var pings = new List<PingMessage>();
        service.OnPingMessageReady += (_, ping) => pings.Add((PingMessage)ping);

        // Act
        var pingTask = service.PingAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        service.HandlePong(new PongMessage(Assert.Single(pings).Payload.NumPongBytes));

        // Assert
        Assert.True(await pingTask);
    }

    [Fact]
    public async Task Given_APingInFlight_When_PingAsync_Then_ItJoinsItAndNoSecondPingGoesOut()
    {
        // Arrange - one ping at a time, so a pong is always checked against the ping it answers
        var service = CreateService(TimeSpan.FromSeconds(30));
        var pings = new List<PingMessage>();
        service.OnPingMessageReady += (_, ping) => pings.Add((PingMessage)ping);
        var first = service.PingAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act
        var second = service.PingAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        service.HandlePong(new PongMessage(Assert.Single(pings).Payload.NumPongBytes));

        // Assert
        Assert.True(await first);
        Assert.True(await second);
        Assert.Single(pings);
    }

    [Fact]
    public async Task Given_APongAlreadyReceived_When_PingAsyncAgain_Then_ANewPingGoesOut()
    {
        // Arrange
        var service = CreateService(TimeSpan.FromSeconds(30));
        var pings = new List<PingMessage>();
        service.OnPingMessageReady += (_, ping) =>
        {
            pings.Add((PingMessage)ping);
            service.HandlePong(new PongMessage(((PingMessage)ping).Payload.NumPongBytes));
        };
        Assert.True(await service.PingAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Act
        var answered = await service.PingAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(answered);
        Assert.Equal(2, pings.Count);
    }

    [Fact]
    public async Task Given_NoPong_When_PingAsyncTimesOut_Then_FalseAndDisconnectEventIsRaised()
    {
        // Arrange - BOLT 1: no pong, MAY close the connection (never fail the channels)
        var service = CreateService(TimeSpan.FromSeconds(30));
        Exception? disconnect = null;
        service.DisconnectEvent += (_, e) => disconnect = e;

        // Act
        var answered = await service.PingAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(answered);
        Assert.IsType<ConnectionException>(disconnect);
    }

    [Fact]
    public async Task Given_NoPong_When_PingAsyncIsCancelled_Then_ItThrowsWithoutDisconnecting()
    {
        // Arrange
        var service = CreateService(TimeSpan.FromSeconds(30));
        var disconnectRaised = false;
        service.DisconnectEvent += (_, _) => disconnectRaised = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PingAsync(TimeSpan.FromSeconds(30), cts.Token));
        Assert.False(disconnectRaised);
    }
}