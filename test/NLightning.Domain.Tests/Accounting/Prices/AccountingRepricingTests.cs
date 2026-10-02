namespace NLightning.Domain.Tests.Accounting.Prices;

using Domain.Accounting.Books;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Prices;

/// <summary>
/// The correction of a closed entry for a replaced price (NL-693): each line valued with the price changes by
/// <c>FiatValue(msat, new) - FiatValue(msat, old)</c>, and the balancing line is the realized gain for what left the node,
/// the cost basis for what came in (the closed lots keep their cost), and only the rounding when the asset lines were
/// valued at market too.
/// </summary>
public class AccountingRepricingTests
{
    private const long PriceId = 7;
    private const string Usd = "USD";

    [Fact]
    public void Given_AnInvoiceAtCost_When_Repriced_Then_TheIncomeChangeIsCarriedOnTheCostBasis()
    {
        // Arrange: 0.001 BTC of sales at 50,000 (the channel line at its lot's cost, no price)
        var sales = Line(AccountRole.Received, "income:sales", -100_000_000, -50m, PriceId);
        var channel = Line(AccountRole.Channels, "assets:lightning:channels", 100_000_000, 50m, null);

        // Act
        var corrections = AccountingRepricing.Corrections([new RepricedLine(sales, sales.AmountMsat, sales.Account)],
                                                          [sales, channel], PriceId, 50_000m, 45_000m, Usd,
                                                          FinancialChart.Default);

        // Assert
        Assert.Equal([("income:sales", 5m, (long?)PriceId), ("assets:cost-basis", -5m, null)],
                     corrections.Select(c => (c.AccountName!, c.FiatAmount!.Value, c.PriceId)));
        Assert.All(corrections, c => Assert.Equal(0, c.AmountMsat));
        Assert.All(corrections, c => Assert.Equal(Usd, c.FiatCurrency));
        Assert.Equal(AccountRole.Channels, corrections[1].Account);
    }

    [Theory]
    [InlineData(45_000, "expenses:losses:realized", 2.5)]
    [InlineData(55_000, "income:gains:realized", -2.5)]
    public void Given_APaymentAtCost_When_Repriced_Then_TheProceedsChangeMovesTheRealizedGain(
        int newPrice, string gainAccount, double gainFiat)
    {
        // Arrange: 0.0005 BTC paid at 50,000, its lots relieved at cost
        var sent = Line(AccountRole.Sent, "expenses:payments", 50_000_000, 25m, PriceId);
        var channel = Line(AccountRole.Channels, "assets:lightning:channels", -50_000_000, -20m, null);
        var gain = Line(AccountRole.Channels, "income:gains:realized", 0, -5m, null);

        // Act
        var corrections = AccountingRepricing.Corrections([new RepricedLine(sent, sent.AmountMsat, sent.Account)],
                                                          [sent, channel, gain], PriceId, 50_000m, newPrice, Usd,
                                                          FinancialChart.Default);

        // Assert
        Assert.Equal(2, corrections.Count);
        Assert.Equal(("expenses:payments", (decimal)-gainFiat), (corrections[0].AccountName!, corrections[0].FiatAmount!.Value));
        Assert.Equal((gainAccount, (decimal)gainFiat), (corrections[1].AccountName!, corrections[1].FiatAmount!.Value));
        Assert.Equal(0m, corrections.Sum(c => c.FiatAmount!.Value));
    }

    [Fact]
    public void Given_AnEntryWithItsAssetLinesAtMarket_When_Repriced_Then_OnlyTheRoundingIsBalanced()
    {
        // Arrange: an entry projected without lot costs (every line at market), 333 msat at a price that rounds
        var sales = Line(AccountRole.Received, "income:sales", -333, -0.00016650m, PriceId);
        var channel = Line(AccountRole.Channels, "assets:lightning:channels", 333, 0.00016650m, PriceId);

        // Act
        var corrections = AccountingRepricing.Corrections(
                              [new RepricedLine(sales, sales.AmountMsat, sales.Account),
                               new RepricedLine(channel, channel.AmountMsat, channel.Account)],
                              [sales, channel], PriceId, 50_000m, 45_000.33333333m, Usd, FinancialChart.Default);

        // Assert: the two changes cancel; no balancing line
        Assert.Equal(2, corrections.Count);
        Assert.Equal(0m, corrections.Sum(c => c.FiatAmount!.Value));
        Assert.DoesNotContain(corrections, c => c.AccountName == "assets:cost-basis");
    }

    [Fact]
    public void Given_AReclassificationMove_When_Repriced_Then_TheTwoSidesCancelWithoutABalancingLine()
    {
        // Arrange: a closed sale moved from sales to consulting (both lines valued with the price)
        var outOfSales = Line(AccountRole.Received, "income:sales", 100_000_000, 50m, PriceId);
        var intoConsulting = Line(AccountRole.Received, "income:consulting", -100_000_000, -50m, PriceId);

        // Act
        var corrections = AccountingRepricing.Corrections(
                              [new RepricedLine(outOfSales, outOfSales.AmountMsat, outOfSales.Account),
                               new RepricedLine(intoConsulting, intoConsulting.AmountMsat, intoConsulting.Account)],
                              [outOfSales, intoConsulting], PriceId, 50_000m, 40_000m, Usd, FinancialChart.Default);

        // Assert
        Assert.Equal([("income:sales", -10m), ("income:consulting", 10m)],
                     corrections.Select(c => (c.AccountName!, c.FiatAmount!.Value)));
    }

    [Fact]
    public void Given_ALateValuationLine_When_Repriced_Then_ItIsRevaluedFromThePostingItValued()
    {
        // Arrange: a zero-msat Price adjustment that carried 50 (0.001 BTC at 50,000) to the channel line
        var late = Line(AccountRole.Channels, "assets:lightning:channels", 0, 50m, PriceId);

        // Act
        var corrections = AccountingRepricing.Corrections([new RepricedLine(late, 100_000_000, AccountRole.Channels)],
                                                          [late], PriceId, 50_000m, 40_000m, Usd,
                                                          FinancialChart.Default);

        // Assert: -10 on the channels, +10 on the cost basis (the late valuation of the other line balances it)
        Assert.Equal([("assets:lightning:channels", -10m), ("assets:cost-basis", 10m)],
                     corrections.Select(c => (c.AccountName!, c.FiatAmount!.Value)));
    }

    [Fact]
    public void Given_TheSamePrice_When_Repriced_Then_NoCorrection()
    {
        // Arrange
        var sales = Line(AccountRole.Received, "income:sales", -100_000_000, -50m, PriceId);

        // Act / Assert
        Assert.Empty(AccountingRepricing.Corrections([new RepricedLine(sales, sales.AmountMsat, sales.Account)],
                                                     [sales], PriceId, 50_000m, 50_000m, Usd, FinancialChart.Default));
    }

    private static AccountingPosting Line(AccountRole role, string account, long msat, decimal fiat, long? priceId) =>
        new(role, msat) { AccountName = account, FiatAmount = fiat, FiatCurrency = Usd, PriceId = priceId };
}