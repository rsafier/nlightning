using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Interception;

using Application.Payments.Interception;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Interception;
using Domain.Protocol.Onion.Enums;

/// <summary>NL-1183: the forward interception hub's LND rules (one client, CLTV deltas, resume on disconnect).</summary>
public class HtlcInterceptorHubTests
{
    private readonly HtlcInterceptorHub _hub = new(NullLogger<HtlcInterceptorHub>.Instance);
    private readonly List<ForwardInterceptResolution> _resolutions = [];

    [Fact]
    public void Given_NoClient_When_Intercepting_Then_TheForwardGoesOn()
    {
        // Act / Assert
        Assert.Equal(ForwardInterceptOutcome.NotIntercepted, _hub.Intercept(Forward(1), 100, Record));
    }

    [Fact]
    public void Given_AClient_When_AForwardIsOfferedTwice_Then_ItIsHeldOnceAndSentOnce()
    {
        // Arrange
        var client = new Client();
        using var connection = _hub.Connect(client);

        // Act
        var first = _hub.Intercept(Forward(1), 100, Record);
        var replay = _hub.Intercept(Forward(1), 100, Record);

        // Assert
        Assert.Equal(ForwardInterceptOutcome.Held, first);
        Assert.Equal(ForwardInterceptOutcome.Held, replay);
        Assert.Equal(500U - 19, Assert.Single(client.Offered).AutoFailHeight);
        Assert.Equal(1, _hub.HeldCount);
    }

    [Fact]
    public void Given_AnIncomingHtlcThatExpiresWithinTheInterceptDelta_When_Intercepting_Then_ExpiryTooSoon()
    {
        // Arrange
        using var connection = _hub.Connect(new Client());

        // Act / Assert: 500 < 479 + 22
        Assert.Equal(ForwardInterceptOutcome.ExpiryTooSoon, _hub.Intercept(Forward(1), 479, Record));
        Assert.Equal(ForwardInterceptOutcome.Held, _hub.Intercept(Forward(2), 478, Record));
    }

    [Fact]
    public void Given_TheHeldLimit_When_Intercepting_Then_Full()
    {
        // Arrange
        using var connection = _hub.Connect(new Client(), new HtlcInterceptorSettings { MaxHeld = 1 });
        _hub.Intercept(Forward(1), 100, Record);

        // Act / Assert
        Assert.Equal(ForwardInterceptOutcome.Full, _hub.Intercept(Forward(2), 100, Record));
    }

    [Fact]
    public void Given_AClient_When_ASecondConnects_Then_ItIsRefused()
    {
        // Arrange
        using var connection = _hub.Connect(new Client());

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => _hub.Connect(new Client()));
    }

    [Fact]
    public async Task Given_AnUnknownCircuit_When_Resolving_Then_NotFound()
    {
        // Arrange
        using var connection = _hub.Connect(new Client());

        // Act
        var result = await _hub.ResolveAsync(new ShortChannelId(1, 1, 1), 9, ForwardInterceptResolution.Resume);

        // Assert
        Assert.Equal(InterceptResolveResult.NotFound, result);
    }

    [Fact]
    public async Task Given_HeldForwards_When_TheAutoFailHeightIsReached_Then_TheyFailBack()
    {
        // Arrange
        using var connection = _hub.Connect(new Client());
        _hub.Intercept(Forward(1), 100, Record);
        _hub.Intercept(Forward(2) with { IncomingExpiry = 900 }, 100, Record);

        // Act
        _hub.ExpireHeld(481);
        await WaitUntilAsync(() => _resolutions.Count == 1);

        // Assert
        var resolution = Assert.Single(_resolutions);
        Assert.Equal(ForwardInterceptAction.Fail, resolution.Action);
        Assert.Equal(FailureCode.TemporaryChannelFailure, resolution.FailureCode);
        Assert.Equal(1, _hub.HeldCount);
    }

    [Fact]
    public async Task Given_HeldForwards_When_TheClientDisconnects_Then_TheyAreResumed()
    {
        // Arrange
        var connection = _hub.Connect(new Client());
        _hub.Intercept(Forward(1), 100, Record);
        _hub.Intercept(Forward(2), 100, Record);

        // Act
        connection.Dispose();
        await WaitUntilAsync(() => _resolutions.Count == 2);

        // Assert
        Assert.All(_resolutions, r => Assert.Equal(ForwardInterceptAction.Resume, r.Action));
        Assert.False(_hub.IsActive);
        Assert.Equal(0, _hub.HeldCount);
    }

    [Fact]
    public async Task Given_AFailedResolution_When_Retried_Then_TheHoldAndExpiryProtectionSurvive()
    {
        using var connection = _hub.Connect(new Client());
        var calls = 0;
        _hub.Intercept(Forward(1), 100, _ => ++calls == 1
            ? Task.FromException(new IOException("save failed")) : Task.CompletedTask);

        Assert.Equal(InterceptResolveResult.Failed,
                     await _hub.ResolveAsync(new ShortChannelId(150, 1, 0), 1, ForwardInterceptResolution.Resume));
        Assert.Equal(1, _hub.HeldCount);
        Assert.Equal(InterceptResolveResult.Resolved,
                     await _hub.ResolveAsync(new ShortChannelId(150, 1, 0), 1, ForwardInterceptResolution.Resume));
        Assert.Equal(2, calls);
        Assert.Equal(0, _hub.HeldCount);
    }

    [Fact]
    public async Task Given_AFailedExpiryResolution_When_AnotherBlockArrives_Then_ItIsRetried()
    {
        using var connection = _hub.Connect(new Client());
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _hub.Intercept(Forward(1), 100, resolution =>
        {
            Assert.Equal(ForwardInterceptAction.Fail, resolution.Action);
            if (Interlocked.Increment(ref calls) == 1)
            {
                attempted.SetResult();
                throw new IOException("save failed");
            }
            return Task.CompletedTask;
        });
        _hub.ExpireHeld(481);
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _hub.HeldCount == 1);
        // Retry after the failed callback has returned to the hub.
        await WaitUntilAsync(() =>
        {
            _hub.ExpireHeld(482);
            return _hub.HeldCount == 0;
        });
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Given_AResolutionInProgress_When_DisconnectedAndResolvedAgain_Then_NoConcurrentCallbackRuns()
    {
        var connection = _hub.Connect(new Client());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _hub.Intercept(Forward(1), 100, async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await complete.Task;
        });
        var resolving = _hub.ResolveAsync(new ShortChannelId(150, 1, 0), 1, ForwardInterceptResolution.Resume);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        connection.Dispose();
        _hub.ExpireHeld(481);
        Assert.Equal(InterceptResolveResult.InProgress,
                     await _hub.ResolveAsync(new ShortChannelId(150, 1, 0), 1, ForwardInterceptResolution.Resume));
        Assert.Equal(1, calls);
        complete.SetResult();
        Assert.Equal(InterceptResolveResult.Resolved, await resolving);
        Assert.Equal(0, _hub.HeldCount);
    }

    private Task Record(ForwardInterceptResolution resolution)
    {
        lock (_resolutions)
            _resolutions.Add(resolution);
        return Task.CompletedTask;
    }

    private static InterceptedForward Forward(ulong htlcId) =>
        new(new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray()), htlcId, new ShortChannelId(150, 1, 0),
            new ShortChannelId(151, 1, 0), null, new Hash(new byte[32]), LightningMoney.MilliSatoshis(10_100),
            LightningMoney.MilliSatoshis(10_000), 500, 460, 0, new byte[1366], []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class Client : IHtlcInterceptorClient
    {
        public List<InterceptedForward> Offered { get; } = [];

        public bool TryOffer(InterceptedForward forward)
        {
            Offered.Add(forward);
            return true;
        }
    }
}