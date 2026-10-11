namespace NLightning.Application.Tests.Accounting.Reports;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using static AccountingBooksTestKit;

/// <summary>
/// Liquidity ads fees in the operational reports and exports (NL-850 LA5): a channel where we bought liquidity and one
/// where we sold it, posted by the production rules.
/// </summary>
public class LiquidityFeeReportTests
{
    private const long PaidMsat = 4_500_000;
    private const long EarnedMsat = 6_000_000;

    private readonly AccountingBooksTestKit _kit = new();

    public LiquidityFeeReportTests()
    {
        var at = T0.AddHours(10);
        Post(AccountingEventKind.ChannelFunded, at, 500_000_000, 700_000, Channel(1), Peer(1), "chan:01:funded",
             ("dualFunded", "true"), ("capacitySat", "900000"), ("isInitiator", "true"));
        Post(AccountingEventKind.LiquidityFeePaid, at, -PaidMsat, PaidMsat, Channel(1), Peer(1), "chan:01:liquidity",
             (AccountingDetailKeys.LiquidityRole, AccountingDetailKeys.LiquidityBuyer),
             (AccountingDetailKeys.Kind, AccountingDetailKeys.LiquidityKindOpen),
             (AccountingDetailKeys.RequestedSat, "400000"));
        Post(AccountingEventKind.ChannelFunded, at.AddDays(1), 300_000_000, 300_000, Channel(2), Peer(2),
             "chan:02:funded", ("dualFunded", "true"), ("capacitySat", "800000"), ("isInitiator", "false"));
        Post(AccountingEventKind.LiquidityFeeEarned, at.AddDays(1), EarnedMsat, 0, Channel(2), Peer(2),
             "chan:02:liquidity", (AccountingDetailKeys.LiquidityRole, AccountingDetailKeys.LiquiditySeller),
             (AccountingDetailKeys.Kind, AccountingDetailKeys.LiquidityKindSplice),
             (AccountingDetailKeys.RequestedSat, "300000"));
    }

    [Fact]
    public async Task Given_LiquidityFees_When_TheIncomeStatementIsRead_Then_TheyAreAnIncomeLineAndAnExpenseLine()
    {
        // Act
        var statement = await _kit.CreateReports().GetIncomeStatementAsync(null, null,
                                                                           TestContext.Current.CancellationToken);

        // Assert
        var income = Assert.Single(statement.Income, l => l.Account == AccountRole.LiquidityIncome);
        Assert.Equal(("income:liquidity", EarnedMsat), (income.Name, income.AmountMsat));
        var expense = Assert.Single(statement.Expenses, l => l.Account == AccountRole.LiquidityFees);
        Assert.Equal(("expenses:fees:liquidity", PaidMsat), (expense.Name, expense.AmountMsat));
    }

    [Fact]
    public async Task Given_ALiquidityFeePaid_When_TheFeesReportIsRead_Then_ItIsAFeeAccount()
    {
        // Act
        var fees = await _kit.CreateReports().GetFeesReportAsync(null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PaidMsat, Assert.Single(fees.Accounts, a => a.Account == AccountRole.LiquidityFees).AmountMsat);
        Assert.Equal(PaidMsat + 1_000_000, fees.TotalMsat);
    }

    [Fact]
    public async Task Given_LiquidityFees_When_TheChannelsReportIsRead_Then_EachChannelShowsItsFeeInItsNet()
    {
        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, null,
                                                                      TestContext.Current.CancellationToken);

        // Assert
        var bought = Assert.Single(report.Channels, c => c.ChannelId == Channel(1));
        Assert.Equal((PaidMsat, 0L), (bought.LiquidityFeesPaidMsat, bought.LiquidityFeesEarnedMsat));
        Assert.Equal(-PaidMsat - 700_000, bought.NetMsat);
        var sold = Assert.Single(report.Channels, c => c.ChannelId == Channel(2));
        Assert.Equal((0L, EarnedMsat), (sold.LiquidityFeesPaidMsat, sold.LiquidityFeesEarnedMsat));
        Assert.Equal(EarnedMsat - 300_000, sold.NetMsat);
        Assert.Equal(EarnedMsat, Assert.Single(report.Peers, p => p.Counterparty == Peer(2)).LiquidityFeesEarnedMsat);
    }

    [Fact]
    public async Task Given_AnRbfThatReplacedThePurchase_When_TheChannelsReportIsRead_Then_TheReplacedFeeIsTakenOff()
    {
        // Arrange: the purchase of channel 1 replaced (its reversal staged by the RBF)
        var original = _kit.Feed.Events.Single(e => e.EventKey == "chan:01:liquidity");
        Post(AccountingEventKind.Reversal, T0.AddDays(2), PaidMsat, -PaidMsat, Channel(1), Peer(1),
             AccountingEventKeys.Replaced(original.EventKey),
             (AccountingConfirmations.ReversesDetail, original.EventKey),
             (AccountingConfirmations.OriginalKindDetail, nameof(AccountingEventKind.LiquidityFeePaid)));

        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, null,
                                                                      TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, Assert.Single(report.Channels, c => c.ChannelId == Channel(1)).LiquidityFeesPaidMsat);
    }

    [Theory]
    [InlineData(AccountingExportFormat.Hledger, "expenses:fees:liquidity", "income:liquidity")]
    [InlineData(AccountingExportFormat.Beancount, "Expenses:Fees:Liquidity", "Income:Liquidity")]
    [InlineData(AccountingExportFormat.Csv, "expenses:fees:liquidity", "income:liquidity")]
    public async Task Given_LiquidityFees_When_Exported_Then_TheirLinesAndKindsAreThere(
        AccountingExportFormat format, string expenseAccount, string incomeAccount)
    {
        // Act
        var chunk = await _kit.CreateExports().ExportAsync(new AccountingExportQuery(format, 0, 1_000),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(expenseAccount, chunk.Text, StringComparison.Ordinal);
        Assert.Contains(incomeAccount, chunk.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(AccountingEventKind.LiquidityFeePaid), chunk.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(AccountingEventKind.LiquidityFeeEarned), chunk.Text, StringComparison.Ordinal);
    }

    // A sealed event with the entry the production posting rules give it
    private void Post(AccountingEventKind kind, DateTimeOffset at, long amountMsat, long feeMsat,
                      Domain.Channels.ValueObjects.ChannelId channelId, Domain.Crypto.ValueObjects.CompactPubKey peer,
                      string key, params (string Key, string? Value)[] details)
    {
        var accountingEvent = new AccountingEventModel
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = at,
            ChannelId = channelId,
            Counterparty = peer,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Details = AccountingDetailsCodec.Create(details)
        };
        var postings = AccountingPostingRules.Post(accountingEvent, k => _kit.BooksRepository.Entries
                                                                             .FirstOrDefault(e => e.EventKey == k));
        _kit.Add(kind, at, amountMsat, feeMsat, channelId, peer, accountingEvent.Details, key,
                 postings.Select(p => (p.Account, p.AmountMsat)).ToArray());
    }
}