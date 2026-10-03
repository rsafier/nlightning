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
/// through Tor whenever Tor is on, <c>Hybrid</c> included, through the production registration; NL-868: unless
/// <c>Accounting:Prices:ThroughTor</c> is false (<c>Hybrid</c> only), and a contradiction with the Tor mode is refused.
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

    [Theory]
    [InlineData(TorMode.Off, null, null)]
    [InlineData(TorMode.Off, false, null)]
    [InlineData(TorMode.Hybrid, null, TorMode.Hybrid)]
    [InlineData(TorMode.Hybrid, true, TorMode.Hybrid)]
    [InlineData(TorMode.Hybrid, false, null)]
    [InlineData(TorMode.TorOnly, null, TorMode.TorOnly)]
    [InlineData(TorMode.TorOnly, true, TorMode.TorOnly)]
    public async Task Given_ThroughTorAndATorMode_When_TheSourceIsBuilt_Then_ItsRouteFollowsTheChoice(
        TorMode mode, bool? throughTor, TorMode? expected)
    {
        // Arrange (NL-868)
        await using var provider = BuildProvider(mode, throughTor);

        // Act
        var source = provider.GetRequiredService<HttpPriceSource>();

        // Assert
        Assert.Equal(expected, source.TorRoute);
    }

    [Theory]
    [InlineData(TorMode.Off, true, "Node:Tor:Mode is Off")]
    [InlineData(TorMode.TorOnly, false, "Node:Tor:Mode is TorOnly")]
    public async Task Given_ThroughTorContradictingTheTorMode_When_TheSourceIsBuilt_Then_ItIsRefused(
        TorMode mode, bool throughTor, string expected)
    {
        // Arrange: never a silent change of route (NL-868)
        await using var provider = BuildProvider(mode, throughTor);

        // Act / Assert
        var exception = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<HttpPriceSource>);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task Given_HybridAndThroughTorFalse_When_TheOnionUrlIsAsked_Then_ItStillGoesThroughTor()
    {
        // Arrange (NL-868: false sends clearnet hosts directly; an onion service is only reachable through Tor)
        await using var provider = BuildProvider(TorMode.Hybrid, false, AccountingPriceOptions.MempoolOnionUrl);
        var source = provider.GetRequiredService<HttpPriceSource>();

        // Act
        var price = await source.GetPriceAsync("USD", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(price);
        Assert.Null(source.TorRoute);
        var request = Assert.Single(_proxy.Requests);
        Assert.Equal((new Uri(AccountingPriceOptions.MempoolOnionUrl).Host, 80), (request.Host, request.Port));
    }

    public async ValueTask DisposeAsync() => await _proxy.DisposeAsync();

    private ServiceProvider BuildProvider(TorMode mode, bool? throughTor = null,
                                          string url = "https://prices.invalid/api/v1/historical-price")
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
            Url = url,
            ThroughTor = throughTor
        }));
        services.AddAccountingPriceSources();
        return services.BuildServiceProvider();
    }
}