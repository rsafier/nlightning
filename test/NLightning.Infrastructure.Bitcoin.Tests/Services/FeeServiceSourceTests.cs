using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq.Protected;
using NBitcoin.RPC;

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
    public async Task Given_BitcoindWithoutEstimate_When_Refreshed_Then_NoRateIsCached()
    {
        // Arrange
        var service = new FeeService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceBitcoind,
            CacheFile = "fee-source-test.bin"
        }, new HttpClient(new Mock<HttpMessageHandler>().Object),
                                     NullLogger<FeeService>.Instance,
                                     (_, _, _) => Task.FromResult<decimal?>(null));

        // Act
        var feeRate = await service.GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(feeRate.IsZero);
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

        // Assert
        Assert.True(service.GetCachedFeeRatePerKw().IsZero);
    }

    [Theory]
    [InlineData("Esplora", "sat/vB", 2_500u)]
    [InlineData("Http", "sat/B", 2_500u)]
    [InlineData("Fixed", "sat/vB", 100u)]
    public void Given_InvalidOptions_When_Constructed_Then_ItFailsFast(string source, string unit, uint fixedRate)
    {
        // Arrange
        var options = new FeeEstimationOptions { Source = source, RateUnit = unit, FixedFeeRatePerKw = fixedRate };

        // Act / Assert
        Assert.NotEmpty(options.GetValidationErrors());
        Assert.Throws<InvalidOperationException>(() => CreateService(options,
                                                                    new Mock<HttpMessageHandler>().Object));
    }

    [Fact]
    public void Given_CachedRate_When_TheReturnedValueIsChanged_Then_TheCacheIsNot()
    {
        // Arrange
        var service = CreateService(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceFixed,
            CacheFile = "fee-source-test.bin"
        }, new Mock<HttpMessageHandler>().Object);

        // Act
        var copy = service.GetCachedFeeRatePerKw();
        copy.Satoshi = 99_999;

        // Assert
        Assert.True(service.GetCachedFeeRatePerKw().IsZero);
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