using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq.Protected;

namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Protocol.Interfaces;
using Options;

public class FeeServiceRegistrationTests
{
    [Fact]
    public void Given_AddFeeServices_When_ResolvingIFeeServiceTwice_Then_ItIsOneSingleton()
    {
        // Arrange
        using var provider = BuildProvider(new FeeEstimationOptions { Source = FeeEstimationOptions.SourceFixed });

        // Act
        var first = provider.GetRequiredService<IFeeService>();
        var second = provider.GetRequiredService<IFeeService>();

        // Assert
        Assert.IsType<FeeService>(first);
        Assert.Same(first, second);
        Assert.Same(first, provider.GetRequiredService<FeeService>());
    }

    [Fact]
    public async Task Given_TheHostStartedTheFeeService_When_DustServiceReadsTheCachedRate_Then_ItSeesTheEstimate()
    {
        // Arrange: before the fix every consumer got its own never-started instance and read 0 sat/kw
        using var provider = BuildProvider(new FeeEstimationOptions
        {
            Source = FeeEstimationOptions.SourceFixed,
            FixedFeeRatePerKw = 10_000,
            FallbackFeeRatePerKw = 253
        }, services => services.AddBitcoinInfrastructure());
        var hostInstance = provider.GetRequiredService<IFeeService>();
        await hostInstance.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            // Act
            var dustService = provider.GetRequiredService<IDustService>();
            var consumerInstance = provider.GetRequiredService<IFeeService>();

            // Assert
            Assert.Equal(10_000, consumerInstance.GetCachedFeeRatePerKw().Satoshi);
            Assert.Equal(98UL * 10_000 * 1_000 / 1_000, dustService.CalculateP2WpkhDustLimit()); // 98 vB at the estimate
        }
        finally
        {
            await hostInstance.StopAsync();
        }
    }

    [Fact]
    public async Task Given_APrimaryHandler_When_TheHttpSourceIsRefreshed_Then_TheHandlerIsUsed()
    {
        // Arrange
        var requests = 0;
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
               .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                                                 ItExpr.IsAny<CancellationToken>())
               .Callback(() => requests++)
               .ReturnsAsync(() => new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent("""{ "fastestFee": 20 }""")
               });
        using var provider = BuildProvider(new FeeEstimationOptions(), primaryHandler: handler.Object);

        // Act
        var rate = await provider.GetRequiredService<IFeeService>()
                                 .GetFeeRatePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5_000, rate.Satoshi);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void Given_AnEarlierTypedClientRegistration_When_AddFeeServices_Then_ItIsReplaced()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IFeeService>(_ => new Mock<IFeeService>().Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<FeeEstimationOptions>(o => o.Source = FeeEstimationOptions.SourceFixed);

        // Act
        services.AddFeeServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(IFeeService));
        Assert.Same(provider.GetRequiredService<IFeeService>(), provider.GetRequiredService<IFeeService>());
    }

    private static ServiceProvider BuildProvider(FeeEstimationOptions options,
                                                 Action<IServiceCollection>? configure = null,
                                                 HttpMessageHandler? primaryHandler = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<FeeEstimationOptions>(o =>
        {
            o.Source = options.Source;
            o.FixedFeeRatePerKw = options.FixedFeeRatePerKw;
            o.FallbackFeeRatePerKw = options.FallbackFeeRatePerKw;
            o.CacheFile = "fee-registration-test.bin";
        });
        configure?.Invoke(services);
        services.AddFeeServices(primaryHandler is null ? null : _ => primaryHandler);
        return services.BuildServiceProvider();
    }
}