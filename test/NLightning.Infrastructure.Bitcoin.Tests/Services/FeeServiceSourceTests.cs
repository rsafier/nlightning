using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq.Protected;
using NBitcoin.RPC;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Options;

public class FeeServiceSourceTests
{
    [Fact]
    public async Task Given_MempoolSpaceStyleApi_When_Refreshed_Then_SatPerVByteBecomesSatPerKw()
    {
        // Arrange: the regression of NL-288, 10 sat/vB was read as 10,000 sat/kw
        var (handler, requests) = CreateHandler("""{ "fastestFee": 10, "halfHourFee": 4, "hourFee": 2 }""");
        var service = CreateService(new FeeEstimationOptions
        {
            Url = "https://mutinynet.com/api/v1/fees/recommended",
            CacheFile = "fee-source-test.bin"
        }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2_500, feeRate.Satoshi);
        Assert.Equal("https://mutinynet.com/api/v1/fees/recommended", Assert.Single(requests).ToString());
    }

    [Fact]
    public async Task Given_PreferredFeeRateAndUnit_When_Refreshed_Then_TheyAreUsed()
    {
        // Arrange
        var (handler, _) = CreateHandler("""{ "fastestFee": 10, "hourFee": 8000 }""");
        var service = CreateService(new FeeEstimationOptions
        {
            PreferredFeeRate = "hourFee",
            RateUnit = FeeRateConverter.SatPerKvByte,
            CacheFile = "fee-source-test.bin"
        }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2_000, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_LowFeeNetwork_When_Refreshed_Then_TheRateIsRaisedToTheFloor()
    {
        // Arrange: signets usually answer 1 sat/vB = 250 sat/kw, below the relay floor
        var (handler, _) = CreateHandler("""{ "fastestFee": 1 }""");
        var service = CreateService(new FeeEstimationOptions { CacheFile = "fee-source-test.bin" }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(253, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_OldRateMultiplierSetting_When_Refreshed_Then_ItIsIgnored()
    {
        // Arrange: configuration files written before NL-288 carry "RateMultiplier": 1000
        var (handler, _) = CreateHandler("""{ "fastestFee": 10 }""");
        var service = CreateService(new FeeEstimationOptions
        {
            RateMultiplier = "1000",
            CacheFile = "fee-source-test.bin"
        }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2_500, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_FixedSource_When_Refreshed_Then_TheFixedRateIsReturnedWithoutHttp()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var service = CreateService(new FeeEstimationOptions
        {
            Source = "fixed",
            FixedFeeRatePerKw = 1_234,
            CacheFile = "fee-source-test.bin"
        }, handler.Object);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1_234, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_BitcoindSource_When_Refreshed_Then_EstimateSmartFeeIsConvertedToSatPerKw()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        (int Target, EstimateSmartFeeMode Mode)? call = null;
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            ConfirmationTarget = 3,
            EstimateMode = "economical",
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(handler.Object), NullLogger<FeeService>.Instance,
                                     (target, mode, _) =>
                                     {
                                         call = (target, mode);
                                         return Task.FromResult<decimal?>(12.5m);
                                     });

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3_125, feeRate.Satoshi);
        Assert.Equal((3, EstimateSmartFeeMode.Economical), call);
    }

    [Fact]
    public async Task Given_BitcoindWithoutEstimate_When_Refreshed_Then_TheFallbackRateIsReturnedNotZero()
    {
        // Arrange: estimatesmartfee has no data on a young signet; 0 sat/kw went into open_channel before the fix
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            FallbackFeeRatePerKw = 3_000,
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(new Mock<HttpMessageHandler>().Object),
                                     NullLogger<FeeService>.Instance,
                                     (_, _, _) => Task.FromResult<decimal?>(null));

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3_000, feeRate.Satoshi);
        Assert.Equal(3_000, service.GetCachedFeeRatePerKw().Satoshi);
    }

    [Theory]
    [InlineData(1u, 10_000)] // fastestFee 40 sat/vB
    [InlineData(2u, 5_000)] // halfHourFee 20
    [InlineData(3u, 5_000)]
    [InlineData(6u, 2_500)] // hourFee 10
    [InlineData(36u, 2_500)]
    [InlineData(144u, 1_250)] // economyFee 5
    [InlineData(1_000u, 1_250)]
    public async Task Given_MempoolSpaceBuckets_When_EstimatingForATarget_Then_TheBucketOfTheTargetIsUsed(
        uint target, long expectedPerKw)
    {
        // Arrange (NL-296): one response, every bucket in it
        var (handler, requests) = CreateHandler(
            """{ "fastestFee": 40, "halfHourFee": 20, "hourFee": 10, "economyFee": 5, "minimumFee": 1 }""");
        var service = CreateService(new FeeEstimationOptions { CacheFile = "fee-source-test.bin" }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(target, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedPerKw, feeRate.Satoshi);
        Assert.Single(requests);
    }

    [Fact]
    public async Task Given_ApiWithoutTheTargetsBucket_When_EstimatingForATarget_Then_TheNodeWideRateIsUsed()
    {
        // Arrange: a custom API with only the preferred property
        var (handler, _) = CreateHandler("""{ "fastestFee": 8 }""");
        var service = CreateService(new FeeEstimationOptions { CacheFile = "fee-source-test.bin" }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(36, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2_000, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_BitcoindSource_When_EstimatingForTargets_Then_EstimateSmartFeeIsAskedPerTargetAndCached()
    {
        // Arrange (NL-296): a faster target gets a higher rate
        var calls = new List<int>();
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            ConfirmationTarget = 6,
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(new Mock<HttpMessageHandler>(MockBehavior.Strict).Object),
                                     NullLogger<FeeService>.Instance,
                                     (target, _, _) =>
                                     {
                                         calls.Add(target);
                                         return Task.FromResult<decimal?>(target == 1 ? 50m : 5m);
                                     });
        var ct = TestContext.Current.CancellationToken;

        // Act
        var fast = await service.GetFeeRatePerKwAsync(1, ct);
        var slow = await service.GetFeeRatePerKwAsync(36, ct);
        var fastAgain = await service.GetFeeRatePerKwAsync(1, ct);

        // Assert
        Assert.Equal(12_500, fast.Satoshi);
        Assert.Equal(1_250, slow.Satoshi);
        Assert.Equal(12_500, fastAgain.Satoshi);
        Assert.Equal([1, 36], calls);
    }

    [Fact]
    public async Task Given_BitcoindWithoutATargetEstimate_When_EstimatingForATarget_Then_TheNodeWideRateIsUsed()
    {
        // Arrange: no estimate for 2 blocks, 4 sat/vB for the node-wide target
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            ConfirmationTarget = 6,
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(new Mock<HttpMessageHandler>(MockBehavior.Strict).Object),
                                     NullLogger<FeeService>.Instance,
                                     (target, _, _) => Task.FromResult<decimal?>(target == 6 ? 4m : null));

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(2, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1_000, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_FixedSource_When_EstimatingForATarget_Then_TheFixedRateIsUsed()
    {
        // Arrange
        var service = CreateService(new FeeEstimationOptions
        {
            Source = "fixed",
            FixedFeeRatePerKw = 777,
            CacheFile = "fee-source-test.bin"
        }, new Mock<HttpMessageHandler>(MockBehavior.Strict).Object);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(1, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(777, feeRate.Satoshi);
    }

    [Fact]
    public void Given_ANeverStartedService_When_ReadingTheCachedRate_Then_ItIsTheDefaultFallback()
    {
        // Arrange
        var service = CreateService(new FeeEstimationOptions { CacheFile = "fee-source-test.bin" },
                                    new Mock<HttpMessageHandler>(MockBehavior.Strict).Object);

        // Act
        var feeRate = service.GetCachedFeeRatePerKw();

        // Assert
        Assert.Equal(2_500, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_AnHttpTimeout_When_Refreshed_Then_ItIsLoggedAndTheFallbackIsUsed()
    {
        // Arrange: HttpClient reports its own timeout as a TaskCanceledException while our token is not cancelled
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
               .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                                                 ItExpr.IsAny<CancellationToken>())
               .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured timeout."));
        var logger = new Mock<ILogger<FeeService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var service = new FeeService(new OptionsWrapper<FeeEstimationOptions>(new FeeEstimationOptions
        {
            FallbackFeeRatePerKw = 1_000,
            CacheFile = "fee-source-test.bin"
        }), new HttpClient(handler.Object), logger.Object);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1_000, feeRate.Satoshi);
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                                 It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("timed out")),
                                 It.IsAny<TaskCanceledException>(),
                                 It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Given_AnEarlierEstimate_When_ALaterRefreshFails_Then_TheEstimateIsKept()
    {
        // Arrange
        var answers = new Queue<decimal?>([12m, null]);
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            FallbackFeeRatePerKw = 253,
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(new Mock<HttpMessageHandler>().Object),
                                     NullLogger<FeeService>.Instance,
                                     (_, _, _) => Task.FromResult(answers.Dequeue()));
        await service.RefreshFeeRateAsync(TestContext.Current.CancellationToken);

        // Act
        await service.RefreshFeeRateAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3_000, service.GetCachedFeeRatePerKw().Satoshi);
    }

    [Fact]
    public async Task Given_OurTokenIsCancelled_When_Refreshed_Then_NothingIsLogged()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var logger = new Mock<ILogger<FeeService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(new Mock<HttpMessageHandler>().Object), logger.Object,
                                     (_, _, ct) => Task.FromCanceled<decimal?>(ct));

        // Act
        await service.RefreshFeeRateAsync(cts.Token);

        // Assert
        logger.Verify(l => l.Log(It.IsIn(LogLevel.Warning, LogLevel.Error), It.IsAny<EventId>(),
                                 It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                                 It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public void Given_BitcoindSourceThroughThePublicConstructor_When_Constructed_Then_ItNeedsNoRpcUntilUsed()
    {
        // Arrange
        var bitcoinOptions = new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = "http://127.0.0.1:1",
            RpcUser = "user",
            RpcPassword = "password",
            ZmqHost = "127.0.0.1",
            ZmqBlockPort = 1,
            ZmqTxPort = 1
        });
        var nodeOptions = new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Signet });

        // Act
        var service = new FeeService(new OptionsWrapper<FeeEstimationOptions>(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            CacheFile = "fee-source-test.bin"
        }), new HttpClient(new Mock<HttpMessageHandler>().Object),
                                     NullLogger<FeeService>.Instance, bitcoinOptions, nodeOptions);

        // Assert: no estimate yet, so the fallback
        Assert.Equal(2_500, service.GetCachedFeeRatePerKw().Satoshi);
    }

    [Theory]
    [InlineData("Esplora", "sat/vB", 2_500u, 2_500u)]
    [InlineData("Http", "sat/B", 2_500u, 2_500u)]
    [InlineData("Fixed", "sat/vB", 100u, 2_500u)]
    [InlineData("Http", "sat/vB", 2_500u, 252u)]
    public void Given_InvalidOptions_When_Constructed_Then_ItFailsFast(string source, string unit, uint fixedRate,
                                                                       uint fallbackRate)
    {
        // Arrange
        var options = new FeeEstimationOptions
        {
            Source = source,
            RateUnit = unit,
            FixedFeeRatePerKw = fixedRate,
            FallbackFeeRatePerKw = fallbackRate
        };

        // Act / Assert
        Assert.NotEmpty(options.GetValidationErrors());
        Assert.Throws<InvalidOperationException>(() => CreateService(options,
                                                                    new Mock<HttpMessageHandler>().Object));
    }

    [Fact]
    public void Given_CachedRate_When_ReadRepeatedly_Then_ItStaysTheFallback()
    {
        // Arrange
        var service = CreateService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceFixed,
            CacheFile = "fee-source-test.bin"
        }, new Mock<HttpMessageHandler>().Object);

        // Act
        var cached = service.GetCachedFeeRatePerKw();

        // Assert: still the fallback (never refreshed); LightningMoney is immutable (NL-202),
        // so a caller cannot touch the cached rate through the returned instance
        Assert.Equal(2_500, cached.Satoshi);
        Assert.Equal(2_500, service.GetCachedFeeRatePerKw().Satoshi);
    }

    [Fact]
    public async Task Given_AnAnswerOverTheCap_When_Refreshed_Then_ItIsRefusedAndTheFallbackIsUsed()
    {
        // Arrange - NL-678: a valid rate padded past 64 KiB is never read whole
        var (handler, _) = CreateHandler("{ \"fastestFee\": 40, \"pad\": \""
                                       + new string('x', FeeService.MaxResponseBytes) + "\" }");
        var service = CreateService(new FeeEstimationOptions
        {
            FallbackFeeRatePerKw = 1_000,
            CacheFile = Path.Combine(Path.GetTempPath(), $"fee-cap-{Guid.NewGuid():N}.bin")
        }, handler);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1_000, feeRate.Satoshi);
    }

    [Fact]
    public async Task Given_APostSource_When_Refreshed_Then_TheBodyIsSentAndTheAnswerRead()
    {
        // Arrange - the POST path reads its answer bounded too (NL-678)
        string? sent = null;
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
               .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                                                 ItExpr.IsAny<CancellationToken>())
               .Returns(async (HttpRequestMessage request, CancellationToken ct) =>
               {
                   Assert.Equal(HttpMethod.Post, request.Method);
                   sent = await request.Content!.ReadAsStringAsync(ct);
                   return new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent("{ \"fastestFee\": 40 }")
                   };
               });
        var service = CreateService(new FeeEstimationOptions
        {
            Method = "POST",
            Body = "{\"q\":1}",
            CacheFile = Path.Combine(Path.GetTempPath(), $"fee-post-{Guid.NewGuid():N}.bin")
        }, handler.Object);

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(10_000, feeRate.Satoshi);
        Assert.Equal("{\"q\":1}", sent);
    }

    [Theory]
    [InlineData("http://mempool.space/api/v1/fees/recommended", false, false)]
    [InlineData("http://mempool.space/api/v1/fees/recommended", true, true)]
    [InlineData("http://192.168.1.10:8999/api/v1/fees/recommended", false, true)] // a LAN IP literal (NL-735)
    [InlineData("http://mempool.lan:8999/api/v1/fees/recommended", false, false)]
    [InlineData("http://127.0.0.1:8999/api/v1/fees/recommended", false, true)]
    [InlineData("ftp://mempool.space/fees", true, false)]
    public void Given_AFeeUrl_When_Constructed_Then_PlainHttpIsOnlyAcceptedLocallyOrWhenAllowed(string url, bool allow,
                                                                                              bool valid)
    {
        // Arrange - NL-678
        var options = new FeeEstimationOptions { Url = url, AllowPlainHttp = allow };

        // Act & Assert
        Assert.Equal(valid, options.GetValidationErrors().Count == 0);
        if (!valid)
            Assert.Throws<InvalidOperationException>(() => CreateService(options,
                                                                        new Mock<HttpMessageHandler>().Object));
    }

    [Fact]
    public async Task Given_AServerThatStallsTheBody_When_Refreshed_Then_TheRequestTimesOutAndTheFallbackIsKept()
    {
        // Arrange (NL-732): the headers come at once, the body never ends; HttpClient.Timeout alone stops at the
        // headers with ResponseHeadersRead, so the refresh hung for the life of the process
        var handler = new StallingBodyHttpHandler();
        var options = new FeeEstimationOptions { CacheFile = "fee-source-test.bin", FallbackFeeRatePerKw = 2_500 };
        var service = new FeeService(new OptionsWrapper<FeeEstimationOptions>(options),
                                     new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) },
                                     NullLogger<FeeService>.Instance);

        // Act
        var refresh = service.RefreshFeeRateAsync(TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(30),
                                                              TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(refresh, finished);
        await refresh;
        Assert.True(handler.BodyReadCancelled);
        Assert.Equal(2_500, service.GetCachedFeeRatePerKw().Satoshi);
    }

    private static FeeService CreateService(FeeEstimationOptions options, HttpMessageHandler handler)
    {
        return new FeeService(new OptionsWrapper<FeeEstimationOptions>(options), new HttpClient(handler), NullLogger<FeeService>.Instance);
    }

    private static (HttpMessageHandler Handler, List<Uri> Requests) CreateHandler(string json)
    {
        var requests = new List<Uri>();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
               .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                                                 ItExpr.IsAny<CancellationToken>())
               .Callback((HttpRequestMessage request, CancellationToken _) => requests.Add(request.RequestUri!))
               .ReturnsAsync(() => new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent(json)
               });
        return (handler.Object, requests);
    }
}