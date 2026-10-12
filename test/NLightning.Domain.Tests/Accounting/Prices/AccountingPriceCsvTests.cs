namespace NLightning.Domain.Tests.Accounting.Prices;

using Domain.Accounting.Prices;

/// <summary>
/// The price file format shared by the daemon's price file and <c>prices import</c> (NL-602 A3-T2, D-A11): good lines,
/// a header, comments, ISO times, and every bad line reported with its line number.
/// </summary>
public class AccountingPriceCsvTests
{
    [Fact]
    public void Given_AFileWithAHeaderCommentsAndBlankLines_When_Parsed_Then_ThePricesAreSortedByTime()
    {
        // Arrange
        const string text = "unixSeconds,price\n# hourly USD\n\n1759400400,86048.12\n1759396800, 85990\r\n"
                          + "2025-10-02T12:00:00Z,86100.5\n";

        // Act
        var result = AccountingPriceCsv.Parse(text);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal([1759396800L, 1759400400L, 1759406400L], result.Points.Select(p => p.Time.ToUnixTimeSeconds()));
        Assert.Equal([85990m, 86048.12m, 86100.5m], result.Points.Select(p => p.Price));
    }

    [Fact]
    public void Given_BadLines_When_Parsed_Then_EachIsReportedByLineAndTheGoodOnesAreKept()
    {
        // Arrange: line 1 good, 2 three fields, 3 bad time, 4 bad price, 5 zero, 6 negative, 7 duplicate of 1,
        // 8 before the genesis day, 9 decimal comma, 10 good
        const string text = "1759396800,85990\n"
                          + "1759400400,86048,USD\n"
                          + "yesterday,86000\n"
                          + "1759404000,abc\n"
                          + "1759407600,0\n"
                          + "1759411200,-5\n"
                          + "1759396800,85991\n"
                          + "1000,1\n"
                          + "1759414800,86000,5\n"
                          + "1759418400,86200\n";

        // Act
        var result = AccountingPriceCsv.Parse(text);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(8, result.ErrorCount);
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9], result.Errors.Select(e => e.Line));
        Assert.Contains("expected 2 fields", result.Errors[0].Message);
        Assert.Contains("not a time", result.Errors[1].Message);
        Assert.Contains("not a price", result.Errors[2].Message);
        Assert.Contains("must be positive", result.Errors[3].Message);
        Assert.Contains("must be positive", result.Errors[4].Message);
        Assert.Contains("duplicate time 1759396800 (first at line 1)", result.Errors[5].Message);
        Assert.Contains("out of range", result.Errors[6].Message);
        Assert.Equal("line 2: expected 2 fields 'unixSeconds,price', found 3", result.Errors[0].ToString());
        Assert.Equal([85990m, 86200m], result.Points.Select(p => p.Price));
    }

    [Fact]
    public void Given_MoreBadLinesThanReported_When_Parsed_Then_TheCountHoldsThemAll()
    {
        // Arrange
        var text = string.Join('\n', Enumerable.Range(0, AccountingPriceCsv.MaxReportedErrors + 5).Select(_ => "x,y"));

        // Act
        var result = AccountingPriceCsv.Parse(text);

        // Assert: the first line is not a header here because it is not a header word
        Assert.Equal(AccountingPriceCsv.MaxReportedErrors + 4, result.ErrorCount);
        Assert.Equal(AccountingPriceCsv.MaxReportedErrors, result.Errors.Count);
        Assert.Equal(2, result.Errors[0].Line);
    }

    [Fact]
    public void Given_APriceWithMoreThanEightPlaces_When_Parsed_Then_ItIsRoundedToEight()
    {
        // Act
        var result = AccountingPriceCsv.Parse("1759396800,1.123456785\n1759400400,0.000000001\n");

        // Assert: the second rounds to 0 and is refused
        Assert.Equal(1.12345678m, Assert.Single(result.Points).Price);
        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("rounds to 0", error.Message);
    }

    [Theory]
    [InlineData(1759396800L, "1", true)]
    [InlineData(1000L, "1", false)]
    [InlineData(1759396800L, "0", false)]
    [InlineData(1759396800L, "-1", false)]
    public void Given_APoint_When_Validated_Then_TimeAndPriceAreChecked(long seconds, string price, bool valid)
    {
        // Arrange
        var point = new AccountingPricePoint(DateTimeOffset.FromUnixTimeSeconds(seconds),
                                             decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));

        // Act
        var result = AccountingPriceCsv.TryValidate(point, out var error);

        // Assert
        Assert.Equal(valid, result);
        Assert.Equal(valid, error is null);
    }
}