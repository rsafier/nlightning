using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Protocol.Services;

using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Infrastructure.Exceptions;
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

    private PingPongService CreateService(NodeOptions options) =>
        new(_messageFactoryMock.Object, Options.Create(options));

    [Theory]
    [InlineData("regtest", 15)]
    [InlineData("mainnet", 60)]
    [InlineData("testnet4", 60)]
    [InlineData("mutinynet", 60)]
    public void Given_NoPingIntervalSet_When_Created_Then_TheNetworkDefaultIsTheInterval(string network, int seconds)
    {
        // Arrange (NL-806: was a random 30-300 s; the in-process test nodes run on regtest and so get 15 s)
        var options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve(network) };

        // Act
        var service = CreateService(options);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(seconds), service.PingInterval);
    }

    [Fact]
    public void Given_APingIntervalSet_When_Created_Then_ItIsTheInterval()
    {
        // Arrange
        var options = new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Regtest,
            PingInterval = TimeSpan.FromSeconds(42)
        };

        // Act
        var service = CreateService(options);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(42), service.PingInterval);
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("mainnet")]
    public void Given_AnInterval_When_NextPingDelay_Then_ItStaysWithinTenPercentAndVaries(string network)
    {
        // Arrange
        var service = CreateService(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve(network) });
        var low = service.PingInterval * (1 - PingPongService.PingJitter);
        var high = service.PingInterval * (1 + PingPongService.PingJitter);

        // Act
        var delays = Enumerable.Range(0, 1_000).Select(_ => service.NextPingDelay()).ToList();

        // Assert
        Assert.All(delays, d => Assert.InRange(d, low, high));
        Assert.True(delays.Distinct().Count() > 1, "the delay is jittered");
        Assert.True(delays.Max() - delays.Min() > service.PingInterval * PingPongService.PingJitter,
                    "the jitter goes both ways");
    }

    [Fact]
    public async Task Given_PongsAnswered_When_TheLoopSteps_Then_EachPingFollowsAJitteredIntervalWait()
    {
        // Arrange - stepped: every wait is recorded and released by hand
        var service = CreateService(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest });
        service.PongTimeout = TimeSpan.FromSeconds(30);
        var pings = 0;
        service.OnPingMessageReady += (_, ping) =>
        {
            Interlocked.Increment(ref pings);
            service.HandlePong(new PongMessage(((PingMessage)ping).Payload.NumPongBytes));
        };
        var waits = new List<(TimeSpan Delay, TaskCompletionSource Step)>();
        var waitRequested = new SemaphoreSlim(0);
        service.DelayAsync = (delay, _) =>
        {
            var step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waits)
                waits.Add((delay, step));
            waitRequested.Release();
            return step.Task;
        };
        using var cts = new CancellationTokenSource();

        // Act
        var loop = service.StartPingAsync(cts.Token);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await waitRequested.WaitAsync(s_testTimeout, TestContext.Current.CancellationToken));
            lock (waits)
                waits[^1].Step.SetResult();
        }

        Assert.True(await waitRequested.WaitAsync(s_testTimeout, TestContext.Current.CancellationToken));
        await cts.CancelAsync();
        lock (waits)
            waits[^1].Step.SetCanceled(cts.Token);
        await loop.WaitAsync(s_testTimeout, TestContext.Current.CancellationToken);

        // Assert - a ping at once and one after each of the three released waits, each wait 15 s +/- 10 %
        Assert.Equal(4, pings);
        Assert.Equal(4, waits.Count);
        Assert.All(waits, w => Assert.InRange(w.Delay, TimeSpan.FromSeconds(13.5), TimeSpan.FromSeconds(16.5)));
    }

    [Fact]
    public async Task Given_ASilentPeerAfterAnAnsweredPing_When_TheNextIntervalPasses_Then_ThePongTimeoutDisconnects()
    {
        // Arrange - NL-806: a connection that dies after a good ping is noticed at the next interval plus the pong
        // timeout (BOLT 1: close the connection, never fail the channels)
        var service = CreateService(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest });
        service.PongTimeout = TimeSpan.FromMilliseconds(50);
        var answer = true;
        var pings = 0;
        service.OnPingMessageReady += (_, ping) =>
        {
            Interlocked.Increment(ref pings);
            if (answer)
                service.HandlePong(new PongMessage(((PingMessage)ping).Payload.NumPongBytes));
        };
        var waited = new List<TimeSpan>();
        service.DelayAsync = (delay, _) =>
        {
            waited.Add(delay);
            answer = false; // the peer goes silent during the interval
            return Task.CompletedTask;
        };
        var disconnect = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DisconnectEvent += (_, e) => disconnect.TrySetResult(e);

        // Act
        var loop = service.StartPingAsync(TestContext.Current.CancellationToken);
        var raised = await disconnect.Task.WaitAsync(s_testTimeout, TestContext.Current.CancellationToken);
        await loop.WaitAsync(s_testTimeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<PingTimeoutException>(raised);
        Assert.Equal(2, pings);
        var wait = Assert.Single(waited);
        Assert.InRange(wait, TimeSpan.FromSeconds(13.5), TimeSpan.FromSeconds(16.5));
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
        Assert.IsType<PingTimeoutException>(await disconnectTcs.Task);
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
        Assert.IsType<PingTimeoutException>(disconnect);
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