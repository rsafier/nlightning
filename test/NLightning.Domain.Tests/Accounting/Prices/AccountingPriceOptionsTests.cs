namespace NLightning.Domain.Tests.Accounting.Prices;

using Domain.Accounting.Prices;

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
}