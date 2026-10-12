namespace NLightning.Domain.Tests.Accounting.Prices;

using Domain.Accounting.Prices;

/// <summary>
/// D-A11's valuation rules (NL-602 A3-T2): the nearest price at or before a time within the maximum age, the boundary
/// included, and a posting's fiat value at 8 places.
/// </summary>
public class AccountingValuationTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan s_maxAge = TimeSpan.FromHours(26);

    [Fact]
    public void Given_PricesAroundATime_When_TheNearestIsAsked_Then_TheLatestAtOrBeforeItWins()
    {
        // Arrange
        var prices = new[]
        {
            new AccountingPricePoint(s_at.AddHours(-2), 1m),
            new AccountingPricePoint(s_at.AddMinutes(-30), 2m),
            new AccountingPricePoint(s_at.AddMinutes(1), 3m)
        };

        // Act
        var nearest = AccountingValuation.NearestAtOrBefore(prices, p => p.Time, s_at, s_maxAge);
        var exact = AccountingValuation.NearestAtOrBefore(prices, p => p.Time, s_at.AddMinutes(1), s_maxAge);
        var before = AccountingValuation.NearestAtOrBefore(prices, p => p.Time, s_at.AddHours(-3), s_maxAge);

        // Assert: a later price never values an earlier time
        Assert.Equal(2m, nearest?.Price);
        Assert.Equal(3m, exact?.Price);
        Assert.Null(before);
    }

    [Fact]
    public void Given_APriceExactlyMaxAgeOld_When_TheNearestIsAsked_Then_ItCountsAndOneTickOlderDoesNot()
    {
        // Arrange
        var atBoundary = new[] { new AccountingPricePoint(s_at - s_maxAge, 7m) };
        var pastBoundary = new[] { new AccountingPricePoint(s_at - s_maxAge - TimeSpan.FromTicks(1), 7m) };

        // Act & Assert
        Assert.Equal(7m, AccountingValuation.NearestAtOrBefore(atBoundary, p => p.Time, s_at, s_maxAge)?.Price);
        Assert.Null(AccountingValuation.NearestAtOrBefore(pastBoundary, p => p.Time, s_at, s_maxAge));
        Assert.True(AccountingValuation.IsUsable(s_at - s_maxAge, s_at, s_maxAge));
        Assert.False(AccountingValuation.IsUsable(s_at.AddTicks(1), s_at, s_maxAge));
    }

    [Theory]
    [InlineData(100_000_000_000L, "86048.12", "86048.12")]
    [InlineData(-1_000L, "86048.12", "-0.00086048")]
    [InlineData(1L, "86048", "0.00000086")]
    [InlineData(1L, "4", "0.00000000")]
    [InlineData(150_000L, "100000", "0.15")]
    [InlineData(2_100_000_000_000_000_000L, "999999999999.99999999", "20999999999999999999.79")]
    public void Given_AnAmountAndAPrice_When_Valued_Then_TheFiatIsSignedAndRoundedToEightPlaces(
        long msat, string price, string expected)
    {
        // Act
        var fiat = AccountingValuation.FiatValue(
            msat, decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));

        // Assert
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), fiat);
    }

    [Fact]
    public void Given_ATime_When_ItsHourIsAsked_Then_ItIsTheUtcHourStart()
    {
        // Act
        var hour = AccountingValuation.HourStart(new DateTimeOffset(2026, 10, 2, 14, 59, 59, TimeSpan.FromHours(2)));

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), hour);
        Assert.Equal(TimeSpan.Zero, hour.Offset);
    }
}