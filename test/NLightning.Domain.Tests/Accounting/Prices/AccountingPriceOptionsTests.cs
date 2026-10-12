namespace NLightning.Domain.Tests.Accounting.Prices;

using Domain.Accounting.Prices;
using Domain.Node.Options;

/// <summary><c>Accounting:Prices</c> (NL-602 A3-T2): the defaults of D-A1/D-A11 and what makes them invalid.</summary>
public class AccountingPriceOptionsTests
{
    [Fact]
    public void Given_TheDefaults_When_Validated_Then_TheyAreValidUsdBothAndTwentySixHours()
    {
        // Arrange
        var options = new AccountingPriceOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
        Assert.Equal("USD", options.NormalizedCurrency);
        Assert.Equal(AccountingPriceSourceMode.Both, options.Source);
        Assert.True(options.UsesCsv && options.UsesHttp && options.HasSource);
        Assert.Equal(TimeSpan.FromHours(26), options.MaxAge);
        Assert.Equal("https://mempool.space/api/v1/historical-price", options.Url);
    }

    [Fact]
    public void Given_BadValues_When_Validated_Then_EachIsReported()
    {
        // Arrange
        var options = new AccountingPriceOptions
        {
            Currency = "US",
            Url = "ftp://example",
            MaxAge = TimeSpan.Zero,
            FetchInterval = TimeSpan.FromSeconds(-1),
            MaxFetchesPerRound = -1,
            CsvFile = " "
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(6, errors.Count);
    }

    [Fact]
    public void Given_SourceNone_When_Validated_Then_TheUrlAndFileAreNotChecked()
    {
        // Arrange
        var options = new AccountingPriceOptions
        {
            Source = AccountingPriceSourceMode.None,
            Url = "not a url",
            CsvFile = string.Empty,
            Currency = " eur "
        };

        // Act & Assert
        Assert.Empty(options.GetValidationErrors());
        Assert.False(options.HasSource);
        Assert.Equal("EUR", options.NormalizedCurrency);
    }

    [Theory]
    [InlineData("http://prices.example/api", false, false)]
    [InlineData("http://prices.example/api", true, true)]
    [InlineData("http://127.0.0.1:8999/api/v1/historical-price", false, true)]
    [InlineData("http://localhost:8999/api", false, true)]
    [InlineData("http://mempoolhqx4isw62xs7abwphsq7ldayuidyx2v2oethdhhj6mlo2r6ad.onion/api", false, true)]
    [InlineData("https://prices.example/api", false, true)]
    public void Given_AUrl_When_Validated_Then_PlainHttpIsOnlyAcceptedLocallyOrWhenAllowed(string url, bool allow,
                                                                                         bool valid)
    {
        // Arrange - NL-678
        var options = new AccountingPriceOptions { Url = url, AllowPlainHttp = allow };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
            Assert.Contains("AllowPlainHttp", Assert.Single(errors));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(0.5, false)]
    [InlineData(1.01, true)]
    [InlineData(3, true)]
    [InlineData(1_000_000, true)]
    [InlineData(1_000_001, false)]
    public void Given_AJumpFactor_When_Validated_Then_ItIsOffOrMoreThanOne(double factor, bool valid)
    {
        // Arrange - NL-678
        var options = new AccountingPriceOptions { MaxPriceJumpFactor = (decimal)factor };

        // Act & Assert
        Assert.Equal(valid, options.GetValidationErrors().Count == 0);
    }

    [Theory]
    [InlineData(60_000, 60_000, true)]
    [InlineData(180_000, 60_000, true)]
    [InlineData(180_001, 60_000, false)]
    [InlineData(20_000, 60_000, true)]
    [InlineData(19_999.99, 60_000, false)]
    [InlineData(600, 60_000, false)]
    [InlineData(6_000_000, 60_000, false)]
    public void Given_AFetchedPriceAndAStoredNeighbor_When_Checked_Then_OnlyAJumpWithinTheFactorIsPlausible(
        double price, double reference, bool plausible)
    {
        // Arrange - NL-678: a decimal-point or unit mistake (x100, /100) is refused, a real move is not
        var options = new AccountingPriceOptions();

        // Act & Assert
        Assert.Equal(plausible, options.IsPlausibleNext((decimal)price, (decimal)reference));
    }

    [Fact]
    public void Given_TheCheckOff_When_Checked_Then_AnyPriceIsPlausible()
    {
        // Arrange
        var options = new AccountingPriceOptions { MaxPriceJumpFactor = 0 };

        // Act & Assert
        Assert.True(options.IsPlausibleNext(1m, AccountingPriceCsv.MaxPrice));
        Assert.Equal(AccountingPriceOptions.DefaultMaxPriceJumpFactor, new AccountingPriceOptions().MaxPriceJumpFactor);
    }

    [Theory]
    [InlineData(null, TorMode.Off, false)]
    [InlineData(null, TorMode.Hybrid, true)]
    [InlineData(null, TorMode.TorOnly, true)]
    [InlineData(true, TorMode.Off, false)]
    [InlineData(true, TorMode.Hybrid, true)]
    [InlineData(true, TorMode.TorOnly, true)]
    [InlineData(false, TorMode.Off, false)]
    [InlineData(false, TorMode.Hybrid, false)]
    [InlineData(false, TorMode.TorOnly, true)]
    public void Given_ThroughTorAndATorMode_When_TheRouteIsAsked_Then_UnsetFollowsTorAndTorOnlyAlwaysUsesIt(
        bool? throughTor, TorMode mode, bool expected)
    {
        // Arrange (NL-868)
        var options = new AccountingPriceOptions { ThroughTor = throughTor };

        // Act
        var routes = options.RoutesThroughTor(new TorOptions { Mode = mode });

        // Assert
        Assert.Equal(expected, routes);
    }

    [Theory]
    [InlineData(null, TorMode.Off, null)]
    [InlineData(null, TorMode.Hybrid, null)]
    [InlineData(null, TorMode.TorOnly, null)]
    [InlineData(true, TorMode.Off, "Node:Tor:Mode is Off")]
    [InlineData(true, TorMode.Hybrid, null)]
    [InlineData(true, TorMode.TorOnly, null)]
    [InlineData(false, TorMode.Off, null)]
    [InlineData(false, TorMode.Hybrid, null)]
    [InlineData(false, TorMode.TorOnly, "Node:Tor:Mode is TorOnly")]
    public void Given_ThroughTorAndATorMode_When_Validated_Then_OnlyTrueWithoutTorAndFalseInTorOnlyAreRefused(
        bool? throughTor, TorMode mode, string? expected)
    {
        // Arrange (NL-868)
        var options = new AccountingPriceOptions { ThroughTor = throughTor };

        // Act
        var errors = options.GetTorRoutingErrors(new TorOptions { Mode = mode });

        // Assert
        if (expected is null)
        {
            Assert.Empty(errors);
            return;
        }

        var error = Assert.Single(errors);
        Assert.Contains(expected, error);
        Assert.Contains("Accounting:Prices:ThroughTor", error);
    }

    [Theory]
    [InlineData(true, TorMode.Off)]
    [InlineData(false, TorMode.TorOnly)]
    public void Given_NoHttpSource_When_ThroughTorContradictsTor_Then_ItIsNotAnError(bool throughTor, TorMode mode)
    {
        // Arrange: the route of a source that is never asked does not matter (NL-868)
        var options = new AccountingPriceOptions { ThroughTor = throughTor, Source = AccountingPriceSourceMode.Csv };

        // Act / Assert
        Assert.Empty(options.GetTorRoutingErrors(new TorOptions { Mode = mode }));
        Assert.Empty(options.GetTorRoutingErrors(null));
    }

    [Fact]
    public void Given_TheOnionUrl_When_Validated_Then_ItIsAllowedAsPlainHttpToAnOnionHost()
    {
        // Arrange (NL-868: the recommended URL with Tor)
        var options = new AccountingPriceOptions { Url = AccountingPriceOptions.MempoolOnionUrl };

        // Act / Assert
        Assert.Empty(options.GetValidationErrors());
        Assert.Null(options.ThroughTor);
    }
}