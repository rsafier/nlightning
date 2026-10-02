using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Mocks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Accounting.Prices;

using Bitcoin.Accounting.Prices;
using Bitcoin.Services;
using Domain.Accounting.Prices;
using Domain.Node.Options;
using Infrastructure.Transport.Tor;

/// <summary>
/// NL-677 (SECURITY_REVIEW SR-21): the price source's requests mark the hours in which the node moved money, so they go
/// through Tor whenever Tor is on, <c>Hybrid</c> included, through the production registration.
/// </summary>
public sealed class PriceSourceTorRoutingTests : IAsyncDisposable
{
    private readonly FakeSocks5Proxy _proxy = new();

    [Theory]
    [InlineData(TorMode.Hybrid)]
    [InlineData(TorMode.TorOnly)]
    public async Task Given_TorOn_When_APriceIsAsked_Then_TheRequestGoesThroughTorByName(TorMode mode)
    {
        // Arrange
        await using var provider = BuildProvider(mode);
        var source = provider.GetRequiredService<HttpPriceSource>();

        // Act: the fake proxy refuses the connection, so there is no answer
        var price = await source.GetPriceAsync("USD", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // Assert: Tor was asked for the host by name (never resolved locally)
        Assert.Null(price);
        var request = Assert.Single(_proxy.Requests);
        Assert.Equal(("prices.invalid", 443), (request.Host, request.Port));
    }

    public async ValueTask DisposeAsync() => await _proxy.DisposeAsync();

    private ServiceProvider BuildProvider(TorMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var nodeOptions = MsOptions.Create(new NodeOptions
        {
            Tor = new TorOptions
            {
                Mode = mode,
                SocksProxy = _proxy.EndPoint,
                OnionServiceEnabled = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            }
        });
        services.AddSingleton(nodeOptions);
        services.AddSingleton<ITorSocksDialer, TorSocksDialer>();
        services.AddSingleton(MsOptions.Create(new AccountingPriceOptions
        {
            Source = AccountingPriceSourceMode.Http,
            Url = "https://prices.invalid/api/v1/historical-price"
        }));
        services.AddAccountingPriceSources();
        return services.BuildServiceProvider();
    }
}