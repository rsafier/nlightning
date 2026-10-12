namespace NLightning.Domain.Tests.Accounting.Financial.Lots;

using Domain.Accounting.Financial.Lots;

/// <summary>The lot file of <c>accounting lots import</c> (NL-602 A3-T4, D-A9): format, errors by line.</summary>
public class AccountingLotCsvTests
{
    [Fact]
    public void Given_AFileWithAHeaderCommentsAndBothTimeForms_When_Parsed_Then_EveryLotIsRead()
    {
        // Arrange
        const string text = "time,sats,cost\n# my lots\n1735689600,100000,40.5\n\n2025-02-01T00:00:00Z,0.5,0\n";

        // Act
        var result = AccountingLotCsv.Parse(text);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(
        [
            new AccountingLotPoint(DateTimeOffset.FromUnixTimeSeconds(1_735_689_600), 100_000_000, 40.5m),
            new AccountingLotPoint(new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero), 500, 0m)
        ], result.Lots);
    }

    [Theory]
    [InlineData("1735689600,100000", "expected 3 fields")]
    [InlineData("yesterday,100000,1", "is not a time")]
    [InlineData("1735689600,1.0001,1", "at most 3 decimals")]
    [InlineData("1735689600,-5,1", "is not an amount")]
    [InlineData("1735689600,0,1", "must be positive")]
    [InlineData("1735689600,10,-1", "is not a cost")]
    [InlineData("1735689600,10,abc", "is not a cost")]
    [InlineData("1000,10,1", "out of range")]
    public void Given_ABadLine_When_Parsed_Then_TheErrorNamesTheLine(string line, string expected)
    {
        // Arrange
        var text = "1735689600,1,1\n" + line + "\n";

        // Act
        var result = AccountingLotCsv.Parse(text);

        // Assert
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains(expected, error.Message);
        Assert.Single(result.Lots);
    }
}