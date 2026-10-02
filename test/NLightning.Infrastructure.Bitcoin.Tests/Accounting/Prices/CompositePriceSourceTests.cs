using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Accounting.Prices;

using Bitcoin.Accounting.Prices;
using Bitcoin.Services;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;

/// <summary>
/// The configured sources in order (NL-602 A3-T2): the price file first, the HTTP source for what it lacks, and the
/// registration that builds no HTTP client at all for <c>Source=None</c> or <c>Csv</c>.
/// </summary>
public class CompositePriceSourceTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_TheFileHasARecentPrice_When_Asked_Then_TheHttpSourceIsNotAsked()
    {
        // Arrange
        var file = new StubSource(Price(s_at.AddMinutes(-10), 1m));
        var http = new StubSource(Price(s_at, 2m));
        var source = new CompositePriceSource([file, http], MsOptions.Create(new AccountingPriceOptions()));

        // Act
        var price = await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1m, price?.Price);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task Given_TheFilePriceIsTooOld_When_Asked_Then_TheHttpSourceAnswers()
    {
        // Arrange
        var file = new StubSource(Price(s_at.AddDays(-3), 1m));
        var http = new StubSource(Price(s_at, 2m));
        var source = new CompositePriceSource([file, http], MsOptions.Create(new AccountingPriceOptions()));

        // Act
        var price = await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2m, price?.Price);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task Given_NoSourceHasARecentPrice_When_Asked_Then_TheLatestAtOrBeforeIsAnswered()
    {
        // Arrange
        var file = new StubSource(Price(s_at.AddDays(-3), 1m));
        var http = new StubSource(Price(s_at.AddDays(-2), 2m));
        var future = new StubSource(Price(s_at.AddHours(1), 3m));
        var source = new CompositePriceSource([file, http, future], MsOptions.Create(new AccountingPriceOptions()));

        // Act
        var price = await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2m, price?.Price);
    }

    [Theory]
    [InlineData(AccountingPriceSourceMode.None, 0)]
    [InlineData(AccountingPriceSourceMode.Csv, 1)]
    [InlineData(AccountingPriceSourceMode.Http, 1)]
    [InlineData(AccountingPriceSourceMode.Both, 2)]
    public async Task Given_ASourceMode_When_Registered_Then_OnlyItsSourcesExistAndNoneMakesNoRequest(
        AccountingPriceSourceMode mode, int sourceCount)
    {
        // Arrange
        var handler = new FakePriceHttpHandler();
        var handlerBuilt = 0;
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(MsOptions.Create(new AccountingPriceOptions
        {
            Source = mode,
            CsvFile = Path.Combine(Path.GetTempPath(), $"nltg-none-{Guid.NewGuid():N}.csv")
        }));
        services.AddAccountingPriceSources(_ =>
        {
            handlerBuilt++;
            return handler;
        });
        await using var provider = services.BuildServiceProvider();

        // Act
        var source = Assert.IsType<CompositePriceSource>(provider.GetRequiredService<IPriceSource>());
        var price = await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(sourceCount, source.Sources.Count);
        if (mode is AccountingPriceSourceMode.None or AccountingPriceSourceMode.Csv)
        {
            Assert.Null(price);
            Assert.Equal(0, handlerBuilt);
            Assert.Empty(handler.Requests);
        }
        else
        {
            Assert.Equal(86_048m, price?.Price);
            Assert.Single(handler.Requests);
        }

        if (mode == AccountingPriceSourceMode.Both)
        {
            Assert.IsType<CsvPriceSource>(source.Sources[0]);
            Assert.IsType<HttpPriceSource>(source.Sources[1]);
        }
    }

    private static AccountingPrice Price(DateTimeOffset time, decimal price) =>
        new(0, "USD", time, price, AccountingPriceSource.Http, time);

    private sealed class StubSource(AccountingPrice? price) : IPriceSource
    {
        public int Calls { get; private set; }

        public Task<AccountingPrice?> GetPriceAsync(string currency, DateTimeOffset time,
                                                    CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(price);
        }
    }
}