namespace NLightning.Domain.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;

/// <summary>
/// The fiat conventions of the financial reports and exports (NL-602 A3-T6): exact values at a BTC price, exact text,
/// currency codes, minor units and the category of a financial account; and the risk-weighted capital view.
/// </summary>
public class AccountingFiatTests
{
    [Theory]
    [InlineData(100_000_000_000L, "86048", "86048")]
    [InlineData(1L, "86048", "0.00000086048")]
    [InlineData(1_000L, "33333.33333333", "0.0003333333333333")]
    [InlineData(-250_000_000L, "40000", "-100")]
    public void Given_AnAmountAndAPrice_When_Valued_Then_TheValueIsExact(long msat, string price, string expected)
    {
        // Act
        var value = AccountingFiat.Value(msat, decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));

        // Assert
        Assert.Equal(expected, AccountingFiat.Format(value));
    }

    [Theory]
    [InlineData("86.050", "86.05")]
    [InlineData("-3.0", "-3")]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("1234567.12345678", "1234567.12345678")]
    public void Given_ADecimal_When_Formatted_Then_ItIsExactWithoutTrailingZeros(string value, string expected)
    {
        // Act
        var text = AccountingFiat.Format(AccountingFiat.Parse(value)!.Value);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(null, "USD")]
    [InlineData(" eur ", "EUR")]
    [InlineData("jpy", "JPY")]
    public void Given_ACurrency_When_Normalized_Then_ItIsThreeUpperCaseLetters(string? currency, string expected) =>
        Assert.Equal(expected, AccountingFiat.NormalizeCurrency(currency));

    [Theory]
    [InlineData("US")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public void Given_ABadCurrency_When_Normalized_Then_ItThrows(string currency) =>
        Assert.Throws<ArgumentException>(() => AccountingFiat.NormalizeCurrency(currency));

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("XYZ", 2)]
    public void Given_ACurrency_When_ItsMinorUnitIsAsked_Then_ItFollowsIso4217(string currency, int units) =>
        Assert.Equal(units, AccountingFiat.MinorUnits(currency));

    [Theory]
    [InlineData("assets:lightning:channels", AccountRole.Sent, AccountingAccountCategory.Assets)]
    [InlineData("Income:Sales", AccountRole.Channels, AccountingAccountCategory.Income)]
    [InlineData("expenses:payroll", AccountRole.Received, AccountingAccountCategory.Expenses)]
    [InlineData("liabilities:loans", AccountRole.Channels, AccountingAccountCategory.Liabilities)]
    [InlineData("sales:customer", AccountRole.Received, AccountingAccountCategory.Income)]
    [InlineData(null, AccountRole.Wallet, AccountingAccountCategory.Assets)]
    public void Given_AFinancialAccount_When_Categorized_Then_ItsRootDecidesElseItsRole(
        string? name, AccountRole role, AccountingAccountCategory expected) =>
        Assert.Equal(expected, AccountingFiat.CategoryOf(name, role));

    [Fact]
    public void Given_ASnapshot_When_RiskWeighted_Then_ZeroStatesAreLeftOutAndWeightsRoundDown()
    {
        // Arrange: 3 msat settled, 3 msat of our offered HTLC in flight (weight 0.5 = 1.5, rounded down to 1)
        var bucket = new ChannelBalanceBucket(new ChannelId(new byte[32]), null, ChannelState.Open, null, 10, 6, 4, 3,
                                              0, 0, 0, 0, true);
        var snapshot = new AccountingSnapshot(DateTimeOffset.UnixEpoch, 1, [bucket],
                                              new WalletBalanceBucket(0, 0, 0));

        // Act
        var report = AccountingRiskCapital.Build(snapshot, AccountingRiskWeights.Default);

        // Assert
        Assert.Equal([AccountingRiskCapital.States.ChannelSettled, AccountingRiskCapital.States.OutgoingInFlight],
                     report.Lines.Select(l => l.State));
        Assert.Equal(3 + 1, report.WeightedMsat);
        Assert.Equal(6, report.GrossMsat);
        Assert.Equal(4, report.ExcludedRemoteMsat);
        Assert.Null(report.WeightedFiat);
        Assert.Equal(4, Assert.Single(report.Channels).WeightedMsat);
    }
}