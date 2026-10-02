using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Accounting.Prices;

using Bitcoin.Accounting.Prices;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;

/// <summary>
/// The operator's price file as a price source (NL-602 A3-T2): read once, read again when it changes, bad lines left
/// out, and nothing for another currency or a missing file.
/// </summary>
public sealed class CsvPriceSourceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nltg-prices-{Guid.NewGuid():N}.csv");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless
        }
    }

    [Fact]
    public async Task Given_APriceFile_When_PricesAreAsked_Then_ItIsReadOnceAndTheNearestAtOrBeforeIsAnswered()
    {
        // Arrange
        await File.WriteAllTextAsync(_path, "unixSeconds,price\n1759402800,85000\n1759406400,86000\n",
                                     TestContext.Current.CancellationToken);
        var source = CreateSource();

        // Act
        var first = await source.GetPriceAsync("USD", DateTimeOffset.FromUnixTimeSeconds(1759408000),
                                               TestContext.Current.CancellationToken);
        var second = await source.GetPriceAsync("USD", DateTimeOffset.FromUnixTimeSeconds(1759403000),
                                                TestContext.Current.CancellationToken);
        var before = await source.GetPriceAsync("USD", DateTimeOffset.FromUnixTimeSeconds(1759400000),
                                                TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(86_000m, first?.Price);
        Assert.Equal(1759406400, first?.Time.ToUnixTimeSeconds());
        Assert.Equal(AccountingPriceSource.Csv, first?.Source);
        Assert.Equal(85_000m, second?.Price);
        Assert.Null(before);
        Assert.Equal(1, source.LoadCount);
    }

    [Fact]
    public async Task Given_TheFileChanged_When_APriceIsAsked_Then_ItIsReadAgain()
    {
        // Arrange
        await File.WriteAllTextAsync(_path, "1759406400,86000\n", TestContext.Current.CancellationToken);
        var source = CreateSource();
        var at = DateTimeOffset.FromUnixTimeSeconds(1759410000);
        Assert.Equal(86_000m, (await source.GetPriceAsync("USD", at, TestContext.Current.CancellationToken))?.Price);

        // Act
        await File.WriteAllTextAsync(_path, "1759406400,86000\n1759409999,87000.5\n",
                                     TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));
        var price = await source.GetPriceAsync("USD", at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(87_000.5m, price?.Price);
        Assert.Equal(2, source.LoadCount);
    }

    [Fact]
    public async Task Given_BadLines_When_TheFileIsRead_Then_TheGoodLinesAreUsed()
    {
        // Arrange
        await File.WriteAllTextAsync(_path, "1759406400,86000\nbroken\n1759409999,-1\n",
                                     TestContext.Current.CancellationToken);
        var source = CreateSource();

        // Act
        var price = await source.GetPriceAsync("USD", DateTimeOffset.FromUnixTimeSeconds(1759410000),
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(86_000m, price?.Price);
    }

    [Fact]
    public async Task Given_AnotherCurrencyOrNoFile_When_APriceIsAsked_Then_ItIsNull()
    {
        // Arrange
        var source = CreateSource();

        // Act
        var missing = await source.GetPriceAsync("USD", DateTimeOffset.FromUnixTimeSeconds(1759410000),
                                                 TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(_path, "1759406400,86000\n", TestContext.Current.CancellationToken);
        var euro = await source.GetPriceAsync("EUR", DateTimeOffset.FromUnixTimeSeconds(1759410000),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(missing);
        Assert.Null(euro);
        Assert.Equal(0, source.LoadCount);
    }

    private CsvPriceSource CreateSource() =>
        new(MsOptions.Create(new AccountingPriceOptions { CsvFile = _path }), NullLogger<CsvPriceSource>.Instance);
}