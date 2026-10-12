using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;
using Gossip;
using Options;

/// <summary>
/// NL-755: one fee fetch at a time, the kept estimate answered at once while it refreshes, a backoff after a failed
/// fetch, and a caller's cancellation that never cancels the shared fetch.
/// </summary>
public class FeeServiceSingleFlightTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Given_AnExpiredEstimateAndAStalledFetch_When_ManyCallersAsk_Then_OneFetchAndTheKeptEstimateAtOnce()
    {
        // Arrange: 10 sat/vB fetched, then the estimate expires and the API stalls
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var handler = new ScriptedHandler("""{ "fastestFee": 10, "hourFee": 4 }""");
        var service = CreateService(new FeeEstimationOptions(), handler, clock);
        await service.RefreshFeeRateAsync(ct);
        handler.Stall();
        clock.Advance(TimeSpan.FromMinutes(6));

        // Act
        var answers = await Task.WhenAll(Enumerable.Range(0, 20)
                                                   .Select(i => i % 2 == 0
                                                                    ? service.GetFeeRatePerKwAsync(ct)
                                                                    : service.GetFeeRatePerKwAsync(6, ct)))
                                .WaitAsync(s_wait, ct);

        // Assert: answered from the kept estimate (node-wide and its hourFee bucket) while one fetch stalls
        Assert.All(answers.Where((_, i) => i % 2 == 0), a => Assert.Equal(2_500, a.Satoshi));
        Assert.All(answers.Where((_, i) => i % 2 == 1), a => Assert.Equal(1_000, a.Satoshi));
        await handler.WaitForRequestsAsync(2, ct);
        Assert.Equal(2, handler.Requests);

        // The stalled fetch ends: its answer replaces the estimate, and still only one fetch ran
        handler.Answer("""{ "fastestFee": 20, "hourFee": 8 }""");
        await service.RefreshFeeRateAsync(ct); // joins the fetch in flight
        Assert.Equal(2, handler.Requests);
        Assert.Equal(5_000, service.GetCachedFeeRatePerKw().Satoshi);
        Assert.Equal(2_000, (await service.GetFeeRatePerKwAsync(6, ct)).Satoshi);
    }

    [Fact]
    public async Task Given_AFailedFetch_When_CallersAskDuringTheBackoff_Then_NoFetchStartsUntilItEnds()
    {
        // Arrange: an estimate, then it expires and the API is down; the forced refresh fails
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var handler = new ScriptedHandler("""{ "fastestFee": 10 }""");
        var service = CreateService(new FeeEstimationOptions(), handler, clock);
        await service.RefreshFeeRateAsync(ct);
        handler.Fail();
        clock.Advance(TimeSpan.FromMinutes(6));
        await service.RefreshFeeRateAsync(ct);
        Assert.Equal(2, handler.Requests);

        // Act: during the backoff
        for (var i = 0; i < 5; i++)
            Assert.Equal(2_500, (await service.GetFeeRatePerKwAsync(ct)).Satoshi);
        var requestsDuringBackoff = handler.Requests;

        // Act: once it ends
        clock.Advance(FeeService.FailedFetchBackoff);
        var afterBackoff = await service.GetFeeRatePerKwAsync(ct);
        await handler.WaitForRequestsAsync(3, ct);

        // Assert
        Assert.Equal(2, requestsDuringBackoff);
        Assert.Equal(2_500, afterBackoff.Satoshi);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Given_NoEstimateAndAStalledFetch_When_OneCallerCancels_Then_TheSharedFetchGoesOnForTheOthers()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler("""{ "fastestFee": 20 }""");
        handler.Stall();
        var service = CreateService(new FeeEstimationOptions(), handler, new ManualTimeProvider());
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaving = service.GetFeeRatePerKwAsync(giveUp.Token);
        var staying = service.GetFeeRatePerKwAsync(ct);
        await handler.WaitForRequestsAsync(1, ct);

        // Act
        await giveUp.CancelAsync();
        var leavingRate = await leaving.WaitAsync(s_wait, ct);
        Assert.False(staying.IsCompleted);
        handler.Answer("""{ "fastestFee": 20 }""");
        var stayingRate = await staying.WaitAsync(s_wait, ct);

        // Assert: the caller that gave up got the fallback, the other one the fetched rate of the one fetch
        Assert.Equal(2_500, leavingRate.Satoshi);
        Assert.Equal(5_000, stayingRate.Satoshi);
        Assert.Equal(1, handler.Requests);
        Assert.False(handler.LastRequestCancelled);
    }

    [Fact]
    public async Task Given_NoEstimate_When_TheFetchFails_Then_TheFallbackIsAnsweredAndTheBackoffHolds()
    {
        // Arrange: the API is down from the start
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var handler = new ScriptedHandler("""{ "fastestFee": 20 }""");
        handler.Fail();
        var service = CreateService(new FeeEstimationOptions { FallbackFeeRatePerKw = 3_000 }, handler, clock);

        // Act
        var first = await service.GetFeeRatePerKwAsync(ct); // waits for the fetch: there is no estimate
        var duringBackoff = await service.GetFeeRatePerKwAsync(ct);
        var requestsDuringBackoff = handler.Requests;
        clock.Advance(FeeService.FailedFetchBackoff);
        handler.Answer("""{ "fastestFee": 20 }""");
        var afterBackoff = await service.GetFeeRatePerKwAsync(ct);

        // Assert: never 0, the fallback until a fetch answers; the caller without an estimate waits for that fetch
        Assert.Equal(3_000, first.Satoshi);
        Assert.Equal(3_000, duringBackoff.Satoshi);
        Assert.Equal(1, requestsDuringBackoff);
        Assert.Equal(5_000, afterBackoff.Satoshi);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Given_ABitcoindTargetWithoutEstimate_When_ManyCallersAskDuringAStalledCall_Then_OneCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int>();
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind
        }, new HttpClient(new ScriptedHandler("{}")), NullLogger<FeeService>.Instance,
                                     async (target, _, _) =>
                                     {
                                         lock (calls)
                                             calls.Add(target);
                                         await release.Task;
                                         return target == 2 ? 40m : 4m;
                                     }, clock);

        // Act
        var answers = Enumerable.Range(0, 10).Select(_ => service.GetFeeRatePerKwAsync(2, ct)).ToList();
        release.SetResult();
        var rates = await Task.WhenAll(answers).WaitAsync(s_wait, ct);

        // Expire the target estimate: kept and answered at once, one call refreshes it
        clock.Advance(TimeSpan.FromMinutes(6));
        var kept = await service.GetFeeRatePerKwAsync(2, ct);

        // Assert
        Assert.All(rates, r => Assert.Equal(10_000, r.Satoshi));
        Assert.Equal(10_000, kept.Satoshi);
        await WaitUntilAsync(() =>
        {
            lock (calls)
                return calls.Count == 2;
        }, ct);
        lock (calls)
            Assert.Equal([2, 2], calls);
    }

    [Fact]
    public async Task Given_ACallerThatAlreadyGaveUp_When_Refreshing_Then_NoFetchStarts()
    {
        // Arrange
        var handler = new ScriptedHandler("""{ "fastestFee": 20 }""");
        var service = CreateService(new FeeEstimationOptions(), handler, new ManualTimeProvider());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        await service.RefreshFeeRateAsync(cts.Token);

        // Assert
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("10s", true)]
    [InlineData("5m", true)]
    [InlineData("24 hours", true)]
    [InlineData("1d", true)]
    [InlineData("9s", false)] // below 10 s
    [InlineData("2d", false)] // above 1 day
    [InlineData("0m", false)]
    [InlineData("300", false)] // no unit: was read as 5 minutes
    [InlineData("1h30m", false)] // was read as 130 "hm", then 5 minutes
    [InlineData("5 min", false)]
    [InlineData("", false)]
    public void Given_ACacheExpiration_When_Validated_Then_OnlyAWellFormedDurationInRangeIsAccepted(string text,
        bool valid)
    {
        // Arrange (NL-756)
        var options = new FeeEstimationOptions { CacheExpiration = text };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        if (valid)
        {
            Assert.Empty(errors);
            return;
        }

        Assert.Contains(errors, e => e.StartsWith("FeeEstimation:CacheExpiration", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => CreateService(options, new ScriptedHandler("{}"),
                                                                    new ManualTimeProvider()));
    }

    [Theory]
    [InlineData("10m", "5m", false)] // shorter than the expiration
    [InlineData("1h", "8d", false)] // over 7 days
    [InlineData("1h", "1h", true)]
    [InlineData("5m", "7d", true)]
    public void Given_ACacheMaxAgeWithACacheFile_When_Validated_Then_ItIsBetweenTheExpirationAndSevenDays(
        string expiration, string maxAge, bool valid)
    {
        // Arrange
        var options = new FeeEstimationOptions
        {
            CacheFile = "fee.bin",
            CacheExpiration = expiration,
            CacheMaxAge = maxAge
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(valid, !errors.Any(e => e.StartsWith("FeeEstimation:CacheMaxAge", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("post", true)]
    [InlineData("PUT", false)]
    public void Given_AnHttpMethod_When_Validated_Then_OnlyGetOrPostIsAccepted(string method, bool valid)
    {
        // Arrange: anything but GET used to be sent as a POST
        var options = new FeeEstimationOptions { Method = method };

        // Act / Assert
        Assert.Equal(valid, options.GetValidationErrors().Count == 0);
    }

    private static FeeService CreateService(FeeEstimationOptions options, HttpMessageHandler handler,
                                            TimeProvider clock) =>
        new(options, new HttpClient(handler), NullLogger<FeeService>.Instance, null, clock);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_wait);
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }

    /// <summary>
    /// A fee API that answers, fails or stalls on command; a stalled request ends with the next
    /// <see cref="Answer"/>, as does every later one.
    /// </summary>
    private sealed class ScriptedHandler(string json) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private string? _json = json;
        private TaskCompletionSource? _stall;
        private int _requests;
        private volatile bool _lastRequestCancelled;

        public int Requests => Volatile.Read(ref _requests);

        public bool LastRequestCancelled => _lastRequestCancelled;

        public void Stall()
        {
            lock (_gate)
                _stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Fail()
        {
            lock (_gate)
                _json = null;
        }

        public void Answer(string answer)
        {
            TaskCompletionSource? stall;
            lock (_gate)
            {
                _json = answer;
                stall = _stall;
                _stall = null;
            }

            stall?.TrySetResult();
        }

        public async Task WaitForRequestsAsync(int count, CancellationToken ct) =>
            await WaitUntilAsync(() => Requests >= count, ct);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            TaskCompletionSource? stall;
            lock (_gate)
                stall = _stall;

            if (stall is not null)
            {
                try
                {
                    await stall.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _lastRequestCancelled = true;
                    throw;
                }
            }

            string? answer;
            lock (_gate)
                answer = _json;

            if (answer is null)
                throw new HttpRequestException("fee API down");

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }
}