namespace NLightning.Domain.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;

/// <summary>
/// The financial chart, its names and the checks of account names and rules (NL-602 A3-T3).
/// </summary>
public class FinancialChartTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_TheDefaultChart_When_Read_Then_ItHasThePlanNamesAndEveryNameIsValid()
    {
        // Arrange
        var chart = FinancialChart.Default;

        // Act
        var names = chart.Names;

        // Assert
        Assert.Equal("income:sales", chart[FinancialAccount.Sales]);
        Assert.Equal("income:routing", chart[FinancialAccount.Routing]);
        Assert.Equal("income:other", chart[FinancialAccount.OtherIncome]);
        Assert.Equal("expenses:payments", chart[FinancialAccount.Payments]);
        Assert.Equal("expenses:losses", chart[FinancialAccount.Losses]);
        Assert.Equal("income:unclassified", chart[FinancialAccount.IncomeUnclassified]);
        Assert.Equal("expenses:unclassified", chart[FinancialAccount.ExpensesUnclassified]);
        Assert.Equal("equity:opening-balances", chart[FinancialAccount.Opening]);
        Assert.Equal("income:gains:realized", chart[FinancialAccount.RealizedGains]);
        Assert.Equal("expenses:losses:realized", chart[FinancialAccount.RealizedLosses]);
        Assert.Equal("assets:lightning:channels", chart[FinancialAccount.Channels]);
        Assert.Equal("expenses:fees:withdraw", chart[FinancialAccount.FeeWithdraw]);
        Assert.Equal(Enum.GetValues<FinancialAccount>().Length, names.Count);
        Assert.All(names.Values, n => Assert.True(FinancialAccountNameRules.TryValidate(n, out _), n));
    }

    [Fact]
    public void Given_EveryOperationalFeeRole_When_MappedByDefault_Then_EachHasItsOwnFeeAccount()
    {
        // Act
        var fees = AccountingAccountCategories.FeeAccounts
                                              .Select(r => FinancialChart.Default[FinancialChart.DefaultAccountOf(r)])
                                              .ToList();

        // Assert
        Assert.Equal(AccountingAccountCategories.FeeAccounts.Count, fees.Distinct().Count());
        Assert.All(fees, f => Assert.StartsWith("expenses:fees:", f));
    }

    [Fact]
    public void Given_RenamedOperationalBucketsAndChartOverrides_When_TheChartIsBuilt_Then_BothApplyAndTyposAreIgnored()
    {
        // Arrange
        var operational = new AccountNames(new Dictionary<AccountRole, string>
        {
            [AccountRole.Wallet] = "assets:btc:cold"
        });
        var overrides = new Dictionary<FinancialAccount, string>
        {
            [FinancialAccount.Sales] = " income:consulting ",
            [FinancialAccount.Payments] = "Expenses:Bad Name",
            [FinancialAccount.Routing] = ""
        };

        // Act
        var chart = new FinancialChart(operational, overrides);

        // Assert
        Assert.Equal("assets:btc:cold", chart[FinancialAccount.Wallet]);
        Assert.Equal("income:consulting", chart[FinancialAccount.Sales]);
        Assert.Equal("expenses:payments", chart[FinancialAccount.Payments]);
        Assert.Equal("income:routing", chart[FinancialAccount.Routing]);
        Assert.Single(chart.IgnoredOverrides);
        Assert.StartsWith("Payments:", chart.IgnoredOverrides[0]);
    }

    [Theory]
    [InlineData("income:sales", true)]
    [InlineData("expenses:fees:on-chain_2026", true)]
    [InlineData("liabilities:customer-deposits", true)]
    [InlineData("income", false)]
    [InlineData("revenue:sales", false)]
    [InlineData("Income:sales", false)]
    [InlineData("income::sales", false)]
    [InlineData("income:sales ", false)]
    [InlineData("income:-sales", false)]
    [InlineData("income:sa les", false)]
    [InlineData("", false)]
    public void Given_AName_When_Validated_Then_OnlyHledgerAndBeancountSafeNamesPass(string name, bool expected)
    {
        // Act
        var valid = FinancialAccountNameRules.TryValidate(name, out var error);

        // Assert
        Assert.Equal(expected, valid);
        Assert.Equal(expected, error is null);
    }

    [Fact]
    public void Given_AnAssetTarget_When_ValidatedAsATarget_Then_ItIsRefused()
    {
        // Act
        var valid = FinancialAccountNameRules.TryValidateTarget("assets:lightning:channels", out var error);

        // Assert
        Assert.False(valid);
        Assert.Contains("asset", error);
    }

    public static TheoryData<string, AccountingRule, bool> RuleCases => new()
    {
        { "a plain rule", Rule(), true },
        { "a regex", Rule() with { LabelPattern = "^inv-[0-9]+$" }, true },
        { "a backreference", Rule() with { LabelPattern = "(a)\\1" }, false },
        { "a lookahead", Rule() with { LabelPattern = "(?=a)" }, false },
        { "an unbalanced pattern", Rule() with { LabelPattern = "(" }, false },
        { "a pattern too long", Rule() with { LabelPattern = new string('a', 257) }, false },
        { "a tag key and glob", Rule() with { TagKey = "customer", TagValue = "acme*" }, true },
        { "a tag glob without a key", Rule() with { TagValue = "acme*" }, false },
        { "an upper-case tag key", Rule() with { TagKey = "Customer" }, false },
        { "a tag key too long", Rule() with { TagKey = new string('k', 33) }, false },
        { "an asset target", Rule() with { TargetAccount = "assets:onchain:wallet" }, false },
        { "an invalid target", Rule() with { TargetAccount = "sales" }, false },
        { "an unknown kind", Rule() with { Kinds = [(AccountingEventKind)999] }, false },
        { "a description too long", Rule() with { Description = new string('d', 1025) }, false }
    };

    [Theory]
    [MemberData(nameof(RuleCases))]
    public void Given_ARule_When_Validated_Then_OnlyAStorableRulePasses(string name, AccountingRule rule,
                                                                       bool expected)
    {
        // Act
        var valid = AccountingRuleValidator.TryValidate(rule, out var error);

        // Assert
        Assert.True(expected == valid, $"{name}: {error}");
    }

    private static AccountingRule Rule() =>
        new(0, 0, null, null, null, null, null, null, null, "income:sales", true, s_at);
}