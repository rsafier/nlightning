using NLightning.Tests.Utils.Accounting;

namespace NLightning.Domain.Tests.Accounting.Books;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;

/// <summary>
/// The liquidity ads fee in the books (NL-850 LA5): <see cref="AccountingEventKind.LiquidityFeePaid"/> and
/// <see cref="AccountingEventKind.LiquidityFeeEarned"/> between the channels and the liquidity expense and income
/// accounts, their reversals, and their financial accounts and lot kinds.
/// </summary>
public class LiquidityFeePostingTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0x5a, 32).ToArray());

    // A purchase of 400,000 sat: 1,500 sat mining fee, 3,000 sat service fee
    private const long FeeMsat = 4_500_000;

    #region Posting rules

    [Fact]
    public void Given_ALiquidityFeePaid_When_Posted_Then_TheChannelsPayTheLiquidityExpense()
    {
        // Act
        var postings = Post(Paid());

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -FeeMsat), (AccountRole.LiquidityFees, FeeMsat));
    }

    [Fact]
    public void Given_ALiquidityFeeEarned_When_Posted_Then_TheChannelsReceiveTheLiquidityIncome()
    {
        // Act
        var postings = Post(Earned());

        // Assert
        AssertPostings(postings, (AccountRole.Channels, FeeMsat), (AccountRole.LiquidityIncome, -FeeMsat));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_AReorgReversalOfALiquidityFee_When_Posted_Then_EveryBalanceIsBackToZero(bool weBought)
    {
        // Arrange
        var books = new BooksSimulator();
        var original = weBought ? Paid() : Earned();
        books.Apply(original);

        // Act
        var negated = books.Apply(AccountingConfirmations.CreateReversal(original, s_at, 99));

        // Assert
        Assert.NotNull(negated);
        Assert.All(books.Balances.Values, balance => Assert.Equal(0, balance));
    }

    [Fact]
    public void Given_APurchaseReplacedByAnRbf_When_TheReplacedReversalIsPosted_Then_OnlyTheNewFeeStays()
    {
        // Arrange: the first attempt's fee, then the RBF's (paid at a higher feerate), and the first one taken back
        var books = new BooksSimulator();
        var first = Paid(blockHeight: null);
        var rbfTxId = new TxId(Enumerable.Repeat((byte)0x6b, 32).ToArray());
        var second = Paid(fundingTxId: rbfTxId, feeMsat: 5_000_000, blockHeight: null);
        books.Apply(first);
        books.Apply(second);
        var replaced = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.Replaced(first.EventKey),
            Kind = AccountingEventKind.Reversal,
            OccurredAt = s_at,
            ChannelId = first.ChannelId,
            TxId = first.TxId,
            AmountMsat = -first.AmountMsat,
            FeeMsat = -first.FeeMsat,
            Finality = AccountingFinality.Final,
            Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, first.EventKey),
                                                   (AccountingConfirmations.OriginalKindDetail,
                                                    nameof(AccountingEventKind.LiquidityFeePaid)))
        };

        // Act
        var entry = books.Apply(replaced);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(-5_000_000, books[AccountRole.Channels]);
        Assert.Equal(5_000_000, books[AccountRole.LiquidityFees]);
    }

    [Fact]
    public void Given_ABuyersDualFundedOpen_When_FundedAndTheFeeArePosted_Then_TheChannelsHoldTheBalanceAfterTheFee()
    {
        // Arrange: our contribution 500,000 sat with a 700 sat funding fee; the seller's fee moves 4,500 sat of it
        var funded = Event(AccountingEventKind.ChannelFunded, 500_000_000, 700_000,
                           AccountingEventKeys.ChannelFunded(Channel(), s_fundingTxId),
                           ("dualFunded", "true"), ("liquidityFeeMsat", FeeMsat.ToString()));

        // Act
        var books = BooksSimulator.Of([funded, Paid()]);

        // Assert: the channel balance is the contribution less the fee, the funding fee and the liquidity fee are
        // expenses, and the wallet side is only the contribution and the funding fee
        Assert.Equal(500_000_000 - FeeMsat, books[AccountRole.Channels]);
        Assert.Equal(FeeMsat, books[AccountRole.LiquidityFees]);
        Assert.Equal(700_000, books[AccountRole.FeeFunding]);
        Assert.Equal(-500_700_000, books[AccountRole.Clearing]);
        Assert.Equal(0, books.Total);
    }

    [Fact]
    public void Given_ASellersDualFundedOpen_When_FundedAndTheFeeArePosted_Then_TheChannelsHoldTheBalanceWithTheFee()
    {
        // Arrange: the seller contributes 400,000 sat and pays 300 sat of the funding fee
        var funded = Event(AccountingEventKind.ChannelFunded, 400_000_000, 300_000,
                           AccountingEventKeys.ChannelFunded(Channel(), s_fundingTxId),
                           ("dualFunded", "true"), ("liquidityFeeMsat", (-FeeMsat).ToString()));

        // Act
        var books = BooksSimulator.Of([funded, Earned()]);

        // Assert
        Assert.Equal(400_000_000 + FeeMsat, books[AccountRole.Channels]);
        Assert.Equal(-FeeMsat, books[AccountRole.LiquidityIncome]);
        Assert.Equal(-400_300_000, books[AccountRole.Clearing]);
        Assert.Equal(0, books.Total);
    }

    [Fact]
    public void Given_LiquidityEvents_When_Described_Then_TheTextNamesTheSideAndThePurchase()
    {
        // Act / Assert
        Assert.Equal("Liquidity fee paid: open, 400000 sat requested", AccountingPostingRules.Describe(Paid()));
        Assert.Equal("Liquidity fee earned: splice, 400000 sat requested",
                     AccountingPostingRules.Describe(Earned(kind: "splice")));
    }

    #endregion

    #region Accounts

    [Fact]
    public void Given_TheLiquidityRoles_When_NamedAndCategorized_Then_TheyAreAnExpenseFeeAndAnIncome()
    {
        // Act / Assert
        Assert.Equal("expenses:fees:liquidity", AccountNames.Default[AccountRole.LiquidityFees]);
        Assert.Equal("income:liquidity", AccountNames.Default[AccountRole.LiquidityIncome]);
        Assert.Equal(AccountingAccountCategory.Expenses, AccountingAccountCategories.Of(AccountRole.LiquidityFees));
        Assert.Equal(AccountingAccountCategory.Income, AccountingAccountCategories.Of(AccountRole.LiquidityIncome));
        Assert.Contains(AccountRole.LiquidityFees, AccountingAccountCategories.FeeAccounts);
        Assert.DoesNotContain(AccountRole.LiquidityIncome, AccountingAccountCategories.FeeAccounts);
    }

    [Fact]
    public void Given_TheFinancialChart_When_TheLiquidityRolesAreMapped_Then_TheIncomeIsClassifiableAndTheFeeIsNot()
    {
        // Act
        var feeAccount = FinancialChart.DefaultAccountOf(AccountRole.LiquidityFees);
        var incomeAccount = FinancialChart.DefaultAccountOf(AccountRole.LiquidityIncome);

        // Assert
        Assert.Equal(FinancialAccount.FeeLiquidity, feeAccount);
        Assert.Equal(FinancialAccount.Liquidity, incomeAccount);
        Assert.Equal("expenses:fees:liquidity", FinancialChart.Default[feeAccount]);
        Assert.Equal("income:liquidity", FinancialChart.Default[incomeAccount]);
        Assert.True(FinancialChart.IsClassifiable(AccountRole.LiquidityIncome));
        Assert.False(FinancialChart.IsClassifiable(AccountRole.LiquidityFees));
    }

    [Fact]
    public void Given_AnEarnedFee_When_ClassifiedByDefault_Then_ItGoesToTheLiquidityIncomeAndARuleCanMoveIt()
    {
        // Arrange
        var earned = Earned();
        var entry = Entry(earned);
        var rule = new AccountingRule(1, 0, [AccountingEventKind.LiquidityFeeEarned], null, null, null, null, null,
                                      null, "income:liquidity:leases", true, s_at);

        // Act
        var byDefault = new ClassificationEngine(FinancialChart.Default, []).Classify(entry, earned, null);
        var byRule = new ClassificationEngine(FinancialChart.Default, [rule]).Classify(entry, earned, null);

        // Assert
        Assert.Equal("income:liquidity", byDefault.Account);
        Assert.False(byDefault.IsUnclassified);
        Assert.Equal("income:liquidity:leases", byRule.Account);
        Assert.Equal(AccountingClassificationSource.Rule, byRule.Source);
    }

    [Fact]
    public void Given_APaidFee_When_ARuleMatchesItsKind_Then_TheFeeKeepsItsChartAccount()
    {
        // Arrange
        var paid = Paid();
        var entry = Entry(paid);
        var rule = new AccountingRule(1, 0, [AccountingEventKind.LiquidityFeePaid], null, null, null, null, null, null,
                                      "expenses:other", true, s_at);
        var engine = new ClassificationEngine(FinancialChart.Default, [rule]);

        // Act
        var lines = engine.MapPostings(entry, paid, engine.Classify(entry, paid, null));

        // Assert
        Assert.Contains(lines, l => l.Account == AccountRole.LiquidityFees
                                 && l.AccountName == "expenses:fees:liquidity");
    }

    [Theory]
    [InlineData(AccountRole.LiquidityIncome, -FeeMsat, FinancialLineKind.Acquisition)]
    [InlineData(AccountRole.LiquidityIncome, FeeMsat, FinancialLineKind.Disposal)]
    [InlineData(AccountRole.LiquidityFees, FeeMsat, FinancialLineKind.Disposal)]
    [InlineData(AccountRole.LiquidityFees, -FeeMsat, FinancialLineKind.Acquisition)]
    public void Given_ALiquidityLine_When_ItsLotKindIsRead_Then_EarnedAcquiresAndPaidDisposes(
        AccountRole role, long amountMsat, FinancialLineKind expected)
    {
        // Arrange
        var line = new AccountingPosting(role, amountMsat) { AccountName = AccountNames.Default[role] };

        // Act
        var kind = FinancialLotRules.KindOf(line);

        // Assert
        Assert.Equal(expected, kind);
    }

    #endregion

    private static IReadOnlyList<AccountingPosting> Post(AccountingEventModel accountingEvent) =>
        AccountingPostingRules.Post(accountingEvent, _ => null);

    private static void AssertPostings(IReadOnlyList<AccountingPosting> postings,
                                       params (AccountRole Account, long AmountMsat)[] expected)
    {
        Assert.Equal(expected.OrderBy(e => e.Account), postings.Select(p => (p.Account, p.AmountMsat))
                                                               .OrderBy(p => p.Account));
        Assert.Equal(0, postings.Sum(p => p.AmountMsat));
    }

    private static ChannelId Channel() => new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private static AccountingEntry Entry(AccountingEventModel accountingEvent) =>
        new(1, accountingEvent.EventKey, accountingEvent.Kind, accountingEvent.OccurredAt, accountingEvent.ChannelId,
            null, Post(accountingEvent));

    private static AccountingEventModel Paid(TxId? fundingTxId = null, long feeMsat = FeeMsat,
                                             uint? blockHeight = 100) =>
        Purchase(AccountingEventKind.LiquidityFeePaid, fundingTxId ?? s_fundingTxId, -feeMsat, feeMsat, "buyer",
                 "open", blockHeight);

    private static AccountingEventModel Earned(string kind = "open") =>
        Purchase(AccountingEventKind.LiquidityFeeEarned, s_fundingTxId, FeeMsat, 0, "seller", kind, 100);

    // In ChannelAccountingEvents.BuildLiquidityPurchase's format
    private static AccountingEventModel Purchase(AccountingEventKind eventKind, TxId fundingTxId, long amountMsat,
                                                 long feeMsat, string role, string kind, uint? blockHeight)
    {
        var total = Math.Abs(amountMsat);
        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.LiquidityFee(Channel(), fundingTxId),
            Kind = eventKind,
            OccurredAt = s_at,
            BlockHeight = blockHeight,
            ChannelId = Channel(),
            TxId = fundingTxId,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Finality = blockHeight is null ? AccountingFinality.Final : AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(
                (AccountingDetailKeys.LiquidityRole, role), (AccountingDetailKeys.Kind, kind),
                (AccountingDetailKeys.RequestedSat, "400000"), (AccountingDetailKeys.ContributedSat, "400000"),
                (AccountingDetailKeys.MiningFeeMsat, (total / 3).ToString()),
                (AccountingDetailKeys.ServiceFeeMsat, (total - total / 3).ToString()))
        };
    }

    private static AccountingEventModel Event(AccountingEventKind kind, long amountMsat, long feeMsat, string key,
                                              params (string Key, string? Value)[] details) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = s_at,
            BlockHeight = 100,
            TxId = s_fundingTxId,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(details)
        };
}