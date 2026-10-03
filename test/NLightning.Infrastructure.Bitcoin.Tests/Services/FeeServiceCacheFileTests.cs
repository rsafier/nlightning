using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;
using Domain.Node.Options;
using Infrastructure.Transport.Tor;
using Options;

/// <summary>
/// The fee rate cache file (NL-706): the last good estimate is saved after a fetch and read back at the start.
/// </summary>
public sealed class FeeServiceCacheFileTests : IDisposable
{
    private const string FeeAnswer = """{ "fastestFee": 10, "halfHourFee": 6, "hourFee": 4, "economyFee": 2 }""";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nltg-fee-cache-{Guid.NewGuid():N}");

    private string CachePath => Path.Combine(_directory, "fee_estimation_cache.bin");

    public FeeServiceCacheFileTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort
        }
    }

    [Fact]
    public async Task Given_ASuccessfulFetch_When_ANewServiceIsBuilt_Then_ItLoadsTheSameEstimateAndBuckets()
    {
        // Arrange: the first node run fetches and saves
        var first = CreateService(Options(), new CountingHandler(FeeAnswer));
        await first.RefreshFeeRateAsync(TestContext.Current.CancellationToken);
        await first.FlushCacheAsync();

        // Act: the next run reads it, and its API is not asked while the estimate is fresh
        var handler = new CountingHandler(FeeAnswer);
        var second = CreateService(Options(), handler);
        await second.CacheLoaded;
        var nodeWide = await second.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);
        var slow = await second.GetFeeRatePerKwAsync(36, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2_500, nodeWide.Satoshi);
        Assert.Equal(1_000, slow.Satoshi); // hourFee 4 sat/vB, from the saved buckets
        Assert.Equal(0, handler.Requests);
        var entry = FeeRateCacheFile.TryRead(CachePath, out var problem);
        Assert.Null(problem);
        Assert.NotNull(entry);
        Assert.Equal(FeeEstimationOptions.SourceHttp, entry.Source);
        Assert.Equal(2_500, entry.FeeRatePerKw);
        Assert.Equal(500, entry.Buckets!["economyFee"]);
    }

    [Fact]
    public async Task Given_ASavedFile_When_Written_Then_ItIsOwnerOnlyAndNoTemporaryFileIsLeft()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");

        // Arrange
        var service = CreateService(Options(), new CountingHandler(FeeAnswer));

        // Act
        await service.RefreshFeeRateAsync(TestContext.Current.CancellationToken);
        await service.FlushCacheAsync();

        // Assert
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CachePath));
        Assert.Equal(new[] { CachePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Given_AFreshSavedEstimate_When_Started_Then_TheStartDoesNotWaitForTheFetch()
    {
        // Arrange: saved a minute ago; the API hangs until released
        WriteEntry(Options(), 4_000, DateTimeOffset.UtcNow.AddMinutes(-1));
        var handler = new HangingHandler(FeeAnswer);
        var service = CreateService(Options(), handler);

        try
        {
            // Act
            await service.StartAsync(TestContext.Current.CancellationToken)
                         .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // Assert: started on the saved rate while the background fetch still hangs
            Assert.Equal(4_000, service.GetCachedFeeRatePerKw().Satoshi);
        }
        finally
        {
            handler.Release();
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Given_AnOlderButUsableEstimate_When_TheFirstFetchFails_Then_ItIsUsedInsteadOfTheFallback()
    {
        // Arrange: saved 30 minutes ago (past CacheExpiration, within CacheMaxAge); the API is down
        WriteEntry(Options(), 4_000, DateTimeOffset.UtcNow.AddMinutes(-30));
        var handler = new CountingHandler(null);
        var service = CreateService(Options(), handler);

        try
        {
            // Act
            await service.StartAsync(TestContext.Current.CancellationToken);

            // Assert: the start tried a fetch (the estimate is not fresh) and kept the saved rate
            Assert.True(handler.Requests >= 1);
            Assert.Equal(4_000, service.GetCachedFeeRatePerKw().Satoshi);
            Assert.Equal(4_000, (await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken)).Satoshi);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Theory]
    [InlineData(-120, "1h")] // older than the default CacheMaxAge
    [InlineData(-20, "10m")] // older than a configured one
    [InlineData(60, "1h")] // fetched in the future (a clock or a file from elsewhere)
    public async Task Given_AStaleOrFutureEstimate_When_Loaded_Then_ItIsIgnoredAndLogged(int minutesFromNow,
        string maxAge)
    {
        // Arrange
        var options = Options();
        options.CacheMaxAge = maxAge;
        WriteEntry(options, 4_000, DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));
        var logger = new RecordingLogger();

        // Act
        var service = CreateService(options, new CountingHandler(null), logger);
        await service.CacheLoaded;

        // Assert
        Assert.Equal(2_500, service.GetCachedFeeRatePerKw().Satoshi); // the fallback
        Assert.Contains(logger.Warnings, w => w.Contains("Ignoring the fee rate cache file"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"Version\": 7, \"FeeRatePerKw\": 4000 }")]
    [InlineData("{ \"Version\": 1, \"FeeRatePerKw\": \"lots\" }")]
    [InlineData("")]
    public async Task Given_ACorruptFile_When_Loaded_Then_ItIsIgnoredAndTheNextFetchReplacesIt(string contents)
    {
        // Arrange
        await File.WriteAllTextAsync(CachePath, contents, TestContext.Current.CancellationToken);
        var logger = new RecordingLogger();

        // Act
        var service = CreateService(Options(), new CountingHandler(FeeAnswer), logger);
        await service.CacheLoaded;
        var beforeFetch = service.GetCachedFeeRatePerKw().Satoshi;
        await service.RefreshFeeRateAsync(TestContext.Current.CancellationToken);
        await service.FlushCacheAsync();

        // Assert
        Assert.Equal(2_500, beforeFetch); // the fallback (equal to the answer's 10 sat/vB, so check the log too)
        Assert.Contains(logger.Warnings, w => w.Contains("Ignoring the fee rate cache file"));
        Assert.NotNull(FeeRateCacheFile.TryRead(CachePath, out _));
    }

    [Theory]
    [InlineData(100L, "below 253")]
    [InlineData(FeeService.MaxCachedFeeRatePerKw + 1, "above")]
    [InlineData(long.MaxValue, "above")]
    public async Task Given_AnImplausibleRate_When_Loaded_Then_ItIsIgnored(long feeRatePerKw, string reason)
    {
        // Arrange
        WriteEntry(Options(), feeRatePerKw, DateTimeOffset.UtcNow);
        var logger = new RecordingLogger();

        // Act
        var service = CreateService(Options(fallback: 3_000), new CountingHandler(null), logger);
        await service.CacheLoaded;

        // Assert
        Assert.Equal(3_000, service.GetCachedFeeRatePerKw().Satoshi);
        Assert.Contains(logger.Warnings, w => w.Contains(reason));
    }

    [Fact]
    public async Task Given_AnOversizedFile_When_Loaded_Then_ItIsIgnored()
    {
        // Arrange
        await File.WriteAllBytesAsync(CachePath, new byte[FeeRateCacheFile.MaxFileBytes + 1],
                                      TestContext.Current.CancellationToken);
        var logger = new RecordingLogger();

        // Act
        var service = CreateService(Options(fallback: 3_000), new CountingHandler(null), logger);
        await service.CacheLoaded;

        // Assert
        Assert.Equal(3_000, service.GetCachedFeeRatePerKw().Satoshi);
        Assert.Contains(logger.Warnings, w => w.Contains("bytes, more than"));
    }

    [Fact]
    public async Task Given_NoFileYet_When_Loaded_Then_TheFallbackAppliesWithoutAWarning()
    {
        // Arrange
        var logger = new RecordingLogger();

        // Act
        var service = CreateService(Options(fallback: 3_000), new CountingHandler(null), logger);
        await service.CacheLoaded;

        // Assert
        Assert.Equal(3_000, service.GetCachedFeeRatePerKw().Satoshi);
        Assert.Empty(logger.Warnings);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Given_AnEstimateFromOtherSourceSettings_When_Loaded_Then_ItIsIgnored()
    {
        // Arrange: saved for the mainnet API, the node now asks mutinynet's
        WriteEntry(Options(), 4_000, DateTimeOffset.UtcNow);
        var options = Options(fallback: 3_000);
        options.Url = "https://mutinynet.com/api/v1/fees/recommended";
        var logger = new RecordingLogger();

        // Act
        var service = CreateService(options, new CountingHandler(null), logger);
        await service.CacheLoaded;

        // Assert
        Assert.Equal(3_000, service.GetCachedFeeRatePerKw().Satoshi);
        Assert.Contains(logger.Warnings, w => w.Contains("another fee source"));
    }

    [Fact]
    public async Task Given_ABitcoindEstimate_When_Reloaded_Then_ItIsUsedForTheSameTargetAndMode()
    {
        // Arrange
        var options = new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            ConfirmationTarget = 3,
            CacheFile = CachePath
        };
        var first = new FeeService(options, new HttpClient(new CountingHandler(null)),
                                   NullLogger<FeeService>.Instance, (_, _, _) => Task.FromResult<decimal?>(12m));
        await first.RefreshFeeRateAsync(TestContext.Current.CancellationToken);
        await first.FlushCacheAsync();

        // Act: bitcoind has no estimate after the restart
        var second = new FeeService(options, new HttpClient(new CountingHandler(null)),
                                    NullLogger<FeeService>.Instance, (_, _, _) => Task.FromResult<decimal?>(null));
        await second.CacheLoaded;

        // Assert
        Assert.Equal(3_000, second.GetCachedFeeRatePerKw().Satoshi);
        Assert.Null(FeeRateCacheFile.TryRead(CachePath, out _)!.Buckets);
    }

    [Fact]
    public async Task Given_ConcurrentWritersOnOneFile_When_TheyAllSave_Then_TheFileIsAlwaysWholeAndNoTempFileIsLeft()
    {
        // Arrange: two services (two processes on one file, or a restart overlapping the old run) refreshing at once
        var services = Enumerable.Range(0, 2)
                                 .Select(_ => CreateService(Options(), new CountingHandler(FeeAnswer)))
                                 .ToArray();
        var readerProblems = new ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(CachePath))
                {
                    try
                    {
                        if (FeeRateCacheFile.TryRead(CachePath, out var problem) is null && problem is not null)
                            readerProblems.Add(problem);
                    }
                    catch (IOException)
                    {
                        // Windows may refuse a read during the rename; never a partial file
                    }
                }

                await Task.Yield();
            }
        }, TestContext.Current.CancellationToken);

        // Act
        await Task.WhenAll(services.SelectMany(s => Enumerable.Range(0, 25)
                                                              .Select(_ => Task.Run(
                                                                   () => s.RefreshFeeRateAsync(
                                                                       TestContext.Current.CancellationToken),
                                                                   TestContext.Current.CancellationToken))));
        foreach (var service in services)
            await service.FlushCacheAsync();
        await stop.CancelAsync();
        await reader;

        // Assert
        Assert.Empty(readerProblems);
        var entry = FeeRateCacheFile.TryRead(CachePath, out var finalProblem);
        Assert.Null(finalProblem);
        Assert.Equal(2_500, entry!.FeeRatePerKw);
        Assert.Equal(new[] { CachePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Given_AnUnwritableCachePath_When_Refreshed_Then_TheEstimateIsStillServedAndTheFailureLogged()
    {
        // Arrange: the cache "directory" is a regular file
        var blocker = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        var options = Options();
        options.CacheFile = Path.Combine(blocker, "fee_estimation_cache.bin");
        var logger = new RecordingLogger();
        var service = CreateService(options, new CountingHandler(FeeAnswer), logger);

        // Act
        var rate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);
        await service.FlushCacheAsync();

        // Assert
        Assert.Equal(2_500, rate.Satoshi);
        Assert.Contains(logger.Warnings, w => w.Contains("Saving the fee rate"));
    }

    [Fact]
    public void Given_ARelativeCacheFile_When_Built_Then_ItResolvesAgainstTheWorkingDirectory()
    {
        // Arrange: hosts other than the daemon (which anchors it to the configuration directory, NL-306)
        var name = $"fee-relative-{Guid.NewGuid():N}.bin";
        var options = Options();
        options.CacheFile = name;

        // Act
        var service = CreateService(options, new CountingHandler(null));

        // Assert
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), name), service.CacheFilePath);
    }

    [Theory]
    [InlineData("", FeeEstimationOptions.SourceHttp)]
    [InlineData("   ", FeeEstimationOptions.SourceHttp)]
    [InlineData("fee.bin", FeeEstimationOptions.SourceFixed)]
    public async Task Given_NoCacheFileOrAFixedRate_When_Refreshed_Then_NothingIsWritten(string cacheFile,
        string source)
    {
        // Arrange
        var options = Options();
        options.Source = source;
        options.CacheFile = cacheFile.Length > 0 && cacheFile.Trim().Length > 0
                                ? Path.Combine(_directory, cacheFile)
                                : cacheFile;
        var service = CreateService(options, new CountingHandler(FeeAnswer));

        // Act
        await service.RefreshFeeRateAsync(TestContext.Current.CancellationToken);
        await service.FlushCacheAsync();

        // Assert
        Assert.Null(service.CacheFilePath);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Given_TorOnlyAndTorNotUpYet_When_Started_Then_TheSavedEstimateIsUsed()
    {
        // Arrange: TorOnly sends the fee request through Tor's SOCKS port, which refuses (Tor still starting); the
        // cache file is local and needs no network
        WriteEntry(Options(), 4_000, DateTimeOffset.UtcNow.AddMinutes(-30));
        var dialer = new Mock<ITorSocksDialer>();
        var dials = 0;
        dialer.Setup(d => d.ConnectAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .Callback(() => Interlocked.Increment(ref dials))
              .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(dialer.Object);
        services.Configure<NodeOptions>(o => o.Tor = new TorOptions { Mode = TorMode.TorOnly });
        services.Configure<FeeEstimationOptions>(o =>
        {
            o.Source = FeeEstimationOptions.SourceHttp;
            o.CacheFile = CachePath;
        });
        services.AddFeeServices();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<FeeService>();

        try
        {
            // Act
            await service.StartAsync(TestContext.Current.CancellationToken);

            // Assert: the fetch went to Tor (and failed), the saved estimate stands
            Assert.True(dials >= 1);
            Assert.Equal(4_000, service.GetCachedFeeRatePerKw().Satoshi);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData(" 1h ", 3_600)]
    [InlineData("2 days", 172_800)]
    [InlineData("1H", 3_600)]
    public void Given_ADuration_When_Parsed_Then_ItIsRead(string text, int seconds)
    {
        Assert.True(FeeEstimationOptions.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("0h")]
    [InlineData("-1h")]
    [InlineData("1y")]
    [InlineData("1h30m")]
    [InlineData("99999999999d")]
    [InlineData("2147483647d")]
    public void Given_AnInvalidMaxAge_When_Validated_Then_ItIsAnErrorOnlyWithACacheFile(string text)
    {
        // Arrange
        var withCache = new FeeEstimationOptions { CacheFile = "fee.bin", CacheMaxAge = text };
        var withoutCache = new FeeEstimationOptions { CacheMaxAge = text };

        // Act / Assert
        Assert.False(FeeEstimationOptions.TryParseDuration(text, out _));
        Assert.Contains(withCache.GetValidationErrors(), e => e.Contains("CacheMaxAge"));
        Assert.Empty(withoutCache.GetValidationErrors());
    }

    private FeeEstimationOptions Options(uint fallback = 2_500) => new()
    {
        Source = FeeEstimationOptions.SourceHttp,
        CacheFile = CachePath,
        FallbackFeeRatePerKw = fallback
    };

    private void WriteEntry(FeeEstimationOptions options, long feeRatePerKw, DateTimeOffset fetchedAt)
    {
        FeeRateCacheFile.Write(CachePath, new FeeRateCacheEntry
        {
            Source = options.Source,
            SourceKey = FeeService.ComputeSourceKey(options),
            FetchedAt = fetchedAt,
            FeeRatePerKw = feeRatePerKw,
            Buckets = new Dictionary<string, long> { ["hourFee"] = feeRatePerKw }
        });
    }

    private static FeeService CreateService(FeeEstimationOptions options, HttpMessageHandler handler,
                                            ILogger<FeeService>? logger = null) =>
        new(new OptionsWrapper<FeeEstimationOptions>(options), new HttpClient(handler),
            logger ?? NullLogger<FeeService>.Instance);

    /// <summary>Answers <c>json</c>, or fails every request when it is null.</summary>
    private sealed class CountingHandler(string? json) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (json is null)
                throw new HttpRequestException("fee API down");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    /// <summary>Holds every request until <see cref="Release"/>.</summary>
    private sealed class HangingHandler(string json) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class RecordingLogger : ILogger<FeeService>
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _warnings.Enqueue(formatter(state, exception));
        }
    }
}