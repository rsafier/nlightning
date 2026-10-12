namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using static FinancialBooksFixture;

/// <summary>
/// The financial book's reports (NL-602 A3-T6) over a hand-computed book on SQLite through the production repositories
/// (<see cref="FinancialBooksFixture"/>): the balance sheet equals the running balances in msat and fiat and balances,
/// the income statement's windows, the realized gains by period (pending valuation, short and long term, estimated
/// basis), the open lots at a given and a stored price, the registers paged through adjustments, the unvalued list,
/// the risk-weighted capital and the refusals.
/// </summary>
public sealed class AccountingFinancialReportServiceTests : IAsyncLifetime
{
    private FinancialBooksFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await CreateAsync();

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Given_TheBook_When_TheBalanceSheetIsAskedNow_Then_ItEqualsTheRunningBalancesAndBalances()
    {
        // Arrange
        var running = await _fixture.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                                   AccountingBook.Financial, TestContext.Current.CancellationToken));

        // Act
        var sheet = await _fixture.CreateReports().GetBalanceSheetAsync(null, "usd", null, false,
                                                                         TestContext.Current.CancellationToken);

        // Assert: wallet 5e7 + 1e9 - 501e6 - 300.2e6; channels 5e8 + 2e8 - 100,010,000 + 5,000 - 1e6 - 2
        Assert.Equal(Usd, sheet.Currency);
        Assert.Equal(10, sheet.ProjectedLedgerSeq);
        var wallet = Assert.Single(sheet.Assets, a => a.Name == "assets:onchain:wallet");
        Assert.Equal(248_800_000, wallet.AmountMsat);
        Assert.Equal(15m + 400m - 200.4m - 120.08m, wallet.FiatAmount);
        var channels = Assert.Single(sheet.Assets, a => a.Name == "assets:lightning:channels");
        Assert.Equal(598_994_998, channels.AmountMsat);
        Assert.Equal(200m + 100m - 35.004m - 0.00000067m, channels.FiatAmount);
        Assert.Equal(2, channels.UnvaluedPostings);
        Assert.True(sheet.IsBalanced);
        Assert.Equal(6, sheet.UnvaluedPostings);
        Assert.Null(sheet.Price);

        // Entry 9's lines were valued one by one and leave 1e-8 in the book: the fiat side is off by exactly that
        Assert.False(sheet.IsFiatBalanced);
        Assert.Equal(-0.00000001m, sheet.TotalAssetsFiat - sheet.TotalLiabilitiesFiat - sheet.TotalEquityFiat
                                 - sheet.RetainedEarningsFiat);

        // Every account equals its running balance, in msat and in fiat (the report totals are the books')
        foreach (var balance in running)
        {
            var category = AccountingFiat.CategoryOf(balance.AccountName, balance.Account);
            if (category is AccountingAccountCategory.Income or AccountingAccountCategory.Expenses)
                continue;

            var sign = category == AccountingAccountCategory.Assets ? 1 : -1;
            var line = sheet.Assets.Concat(sheet.Liabilities).Concat(sheet.Equity)
                            .Single(l => l.Name == balance.AccountName);
            Assert.Equal(sign * balance.BalanceMsat, line.AmountMsat);
            Assert.Equal(sign * balance.FiatAmount, line.FiatAmount);
        }

        var earnings = running.Where(b => AccountingFiat.CategoryOf(b.AccountName, b.Account)
                                                  is AccountingAccountCategory.Income
                                                  or AccountingAccountCategory.Expenses)
                              .ToList();
        Assert.Equal(-earnings.Sum(b => b.BalanceMsat), sheet.RetainedEarningsMsat);
        Assert.Equal(-earnings.Sum(b => b.FiatAmount), sheet.RetainedEarningsFiat);
        _fixture.Projection.Verify(p => p.ProjectAsync(It.IsAny<CancellationToken>()), Times.Once);
        _fixture.Books.Verify(b => b.ProjectNowAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_APastTimeAndAPrice_When_TheBalanceSheetIsAsked_Then_ItCountsEarlierEntriesAtMarketValue()
    {
        // Act: before the payment of 2026-03-02, at 70,000 USD
        var sheet = await _fixture.CreateReports().GetBalanceSheetAsync(At(2026, 3, 1), Usd, 70_000m, false,
                                                                         TestContext.Current.CancellationToken);

        // Assert: opening + deposit + funding + invoice
        Assert.Equal(At(2026, 3, 1), sheet.At);
        Assert.Equal(549_000_000, sheet.Assets.Single(a => a.Name == "assets:onchain:wallet").AmountMsat);
        var channels = sheet.Assets.Single(a => a.Name == "assets:lightning:channels");
        Assert.Equal(700_000_000, channels.AmountMsat);
        Assert.Equal(490m, channels.MarketValue);
        Assert.Equal(0m + 384.3m + 490m, sheet.TotalAssetsMarketValue);
        Assert.True(sheet.Price!.IsGiven);
        Assert.Equal(100m - 0.4m, sheet.RetainedEarningsFiat);
        Assert.True(sheet.IsBalanced);
        Assert.True(sheet.IsFiatBalanced);
        Assert.Equal(0, sheet.UnvaluedPostings);
    }

    [Fact]
    public async Task Given_TheBook_When_TheIncomeStatementIsAsked_Then_ItSumsThePeriodInMsatAndFiat()
    {
        // Act: the first quarter of 2026 and the whole book
        var reports = _fixture.CreateReports();
        var quarter = await reports.GetIncomeStatementAsync(At(2026, 1, 1), At(2026, 4, 1), null,
                                                            TestContext.Current.CancellationToken);
        var all = await reports.GetIncomeStatementAsync(null, null, Usd, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["income:gains:realized", "income:routing", "income:sales"], quarter.Income.Select(l => l.Name));
        Assert.Equal(25.002m, quarter.Income.Single(l => l.Name == "income:gains:realized").FiatAmount);
        Assert.Equal(0, quarter.Income.Single(l => l.Name == "income:gains:realized").AmountMsat);
        Assert.Equal(5_000, quarter.Income.Single(l => l.Name == "income:routing").AmountMsat);
        Assert.Equal(1, quarter.Income.Single(l => l.Name == "income:routing").UnvaluedPostings);
        Assert.Equal(200_000_000, quarter.Income.Single(l => l.Name == "income:sales").AmountMsat);
        Assert.Equal(125.002m, quarter.TotalIncomeFiat);
        Assert.Equal(0.4m + 60m + 0.006m, quarter.TotalExpensesFiat);
        Assert.Equal(101_000_000, quarter.Expenses.Single(l => l.Name == "expenses:payments").AmountMsat);
        Assert.Equal(0, quarter.Expenses.Single(l => l.Name == "expenses:unclassified").AmountMsat);
        Assert.Equal(4, quarter.UnvaluedPostings);
        Assert.Equal(205.122m, all.Income.Single(l => l.Name == "income:gains:realized").FiatAmount);
        Assert.Equal(all.TotalIncomeFiat - all.TotalExpensesFiat, all.NetIncomeFiat);
    }

    [Fact]
    public async Task Given_TheReliefs_When_TheRealizedGainsAreAskedByMonth_Then_EachMonthAndTheTotalAreExact()
    {
        // Act
        var gains = await _fixture.CreateReports().GetRealizedGainsAsync(null, null, AccountingGainsGrouping.Month,
                                                                          Usd, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["2026-01", "2026-03", "2027-03"], gains.Periods.Select(p => p.Period));
        var january = gains.Periods[0];
        Assert.Equal((1, 1_000_000L, 0.4m, 0.4m, 0m), (january.Reliefs, january.DisposedMsat, january.CostBasis,
                                                       january.Proceeds, january.Gain));
        var march = gains.Periods[1];
        Assert.Equal(3, march.Reliefs);
        Assert.Equal(101_010_000, march.DisposedMsat);
        Assert.Equal(35.004m, march.CostBasis);
        Assert.Equal(60.006m, march.Proceeds);
        Assert.Equal(25.002m, march.ShortTermGain);
        Assert.Equal(0m, march.LongTermGain);
        Assert.Equal((1, 1_000_000L), (march.PendingValuation, march.PendingValuationMsat));
        Assert.Equal(50_000_000, march.BasisEstimatedMsat);
        Assert.Equal(At(2026, 3, 1), march.Start);
        Assert.Equal(At(2026, 4, 1), march.End);
        var nextYear = gains.Periods[2];
        Assert.Equal(180.12m, nextYear.LongTermGain);
        Assert.Equal(0m, nextYear.ShortTermGain);
        Assert.Equal(5, gains.Total.Reliefs);
        Assert.Equal(402_210_000, gains.Total.DisposedMsat);
        Assert.Equal(205.122m, gains.Total.Gain);
        Assert.Equal(25.002m + 180.12m, gains.Total.ShortTermGain + gains.Total.LongTermGain);

        // The booked gains equal the reliefs' (the income statement's realized gain account)
        var statement = await _fixture.CreateReports().GetIncomeStatementAsync(null, null, Usd,
                                                                               TestContext.Current.CancellationToken);
        Assert.Equal(gains.Total.Gain, statement.Income.Single(l => l.Name == "income:gains:realized").FiatAmount);
    }

    [Theory]
    [InlineData(AccountingGainsGrouping.Quarter, new[] { "2026-Q1", "2027-Q1" })]
    [InlineData(AccountingGainsGrouping.Year, new[] { "2026", "2027" })]
    [InlineData(AccountingGainsGrouping.Total, new[] { "total" })]
    public async Task Given_AGrouping_When_TheRealizedGainsAreAsked_Then_ThePeriodsFollowIt(
        AccountingGainsGrouping grouping, string[] periods)
    {
        // Act
        var gains = await _fixture.CreateReports().GetRealizedGainsAsync(null, null, grouping, Usd,
                                                                          TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(periods, gains.Periods.Select(p => p.Period));
        Assert.Equal(205.122m, gains.Periods.Sum(p => p.Gain));
    }

    [Fact]
    public async Task Given_AWindow_When_TheRealizedGainsAreAsked_Then_OnlyItsReliefsCount()
    {
        // Act
        var gains = await _fixture.CreateReports().GetRealizedGainsAsync(At(2026, 2, 1), At(2027, 1, 1),
                                                                          AccountingGainsGrouping.Total, Usd,
                                                                          TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, gains.Total.Reliefs);
        Assert.Equal(25.002m, gains.Total.Gain);
        Assert.Equal(At(2026, 2, 1), gains.Total.Start);
    }

    [Fact]
    public async Task Given_AnotherCurrency_When_TheRealizedGainsAreAsked_Then_EveryReliefIsPendingValuation()
    {
        // Act
        var gains = await _fixture.CreateReports().GetRealizedGainsAsync(null, null, AccountingGainsGrouping.Total,
                                                                          "EUR", TestContext.Current.CancellationToken);

        // Assert: never a zero gain for what has no value in EUR
        Assert.Equal(5, gains.Total.PendingValuation);
        Assert.Equal(0m, gains.Total.Gain);
        Assert.Equal("EUR", gains.Currency);
    }

    [Fact]
    public async Task Given_TheOpenLots_When_ListedAtAGivenPrice_Then_TheTotalsAndUnrealizedGainsAreExact()
    {
        // Act
        var report = await _fixture.CreateReports().GetLotsAsync(0, 100, Usd, 100_000m, true,
                                                                  TestContext.Current.CancellationToken);

        // Assert: L1 647,790,000 of 1e9 (cost 259.116), L2 2e8 (cost 100), L3 5,000 unvalued; L4 is used up
        Assert.Equal([1L, 2L, 3L], report.Lots.Select(l => l.Lot.Id));
        Assert.Equal(259.116m, report.Lots[0].RemainingCostBasis);
        Assert.Equal(647.79m, report.Lots[0].MarketValue);
        Assert.Equal(647.79m - 259.116m, report.Lots[0].UnrealizedGain);
        Assert.Null(report.Lots[2].RemainingCostBasis);
        Assert.Null(report.Lots[2].UnrealizedGain);
        Assert.Equal(3, report.Totals.OpenLots);
        Assert.Equal(847_795_000, report.Totals.RemainingMsat);
        Assert.Equal(359.116m, report.Totals.CostBasis);
        Assert.Equal((5_000L, 1), (report.Totals.UnvaluedMsat, report.Totals.UnvaluedLots));
        Assert.Equal(847.795m, report.Totals.MarketValue);
        Assert.Equal(847.79m - 359.116m, report.Totals.UnrealizedGain);
        Assert.Equal(0, report.Totals.BasisEstimatedMsat);
        Assert.False(report.HasMore);
    }

    [Fact]
    public async Task Given_NoPrice_When_TheUnrealizedGainsAreAsked_Then_TheLatestStoredPriceIsUsed()
    {
        // Act
        var report = await _fixture.CreateReports().GetLotsAsync(0, 2, Usd, null, true,
                                                                  TestContext.Current.CancellationToken);

        // Assert: P5, the latest at or before now; a page of two with more after
        Assert.NotNull(report.Price);
        Assert.Equal(_fixture.PriceIds[5], report.Price.PriceId);
        Assert.Equal(At(2027, 3, 21), report.Price.Time);
        Assert.Equal(AccountingFiat.Value(847_795_000, 33_333.33333333m), report.Totals.MarketValue);
        Assert.Equal([1L, 2L], report.Lots.Select(l => l.Lot.Id));
        Assert.True(report.HasMore);
        Assert.Equal(2, report.NextAfterLotId);
        var next = await _fixture.CreateReports().GetLotsAsync(report.NextAfterLotId, 2, Usd, null, false,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal([3L], next.Lots.Select(l => l.Lot.Id));
        Assert.False(next.HasMore);
        Assert.Null(next.Price);
        Assert.Null(next.Totals.MarketValue);
    }

    [Fact]
    public async Task Given_NoStoredPrice_When_TheUnrealizedGainsAreAsked_Then_ItIsRefused()
    {
        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => _fixture.CreateReports().GetLotsAsync(0, 10, "EUR", null, true,
                                                                       TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("--price", exception.Message);
    }

    [Fact]
    public async Task Given_SmallPages_When_TheRegisterIsRead_Then_ThePagesWalkEveryEntryAndAdjustment()
    {
        // Arrange
        var reports = _fixture.CreateReports();
        var keys = new List<(long, int)>();
        long after = 0;
        var afterAdjustment = int.MaxValue;

        // Act
        while (true)
        {
            var page = await reports.GetRegisterAsync(new AccountingEntryQuery(after, 3)
            {
                AfterAdjustment = afterAdjustment
            }, TestContext.Current.CancellationToken);
            keys.AddRange(page.Entries.Select(e => (e.LedgerSeq, e.Adjustment)));
            Assert.All(page.Entries, e => Assert.Equal(AccountingBook.Financial, e.Book));
            if (!page.HasMore)
                break;

            (after, afterAdjustment) = (page.NextAfter, page.NextAfterAdjustment);
        }

        // Assert
        Assert.Equal([(1L, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0), (7, 0), (7, 1), (8, 0), (9, 0), (10, 0)],
                     keys);
    }

    [Fact]
    public async Task Given_FlaggedEntries_When_TheUnclassifiedAreListed_Then_OnlyThoseComeBack()
    {
        // Act
        var page = await _fixture.CreateReports().GetRegisterAsync(new AccountingEntryQuery(0, 100)
        {
            WithFlags = AccountingEntryFlags.Unclassified
        }, TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(page.Entries);
        Assert.Equal((7L, 0), (entry.LedgerSeq, entry.Adjustment));
        Assert.Equal("expenses:unclassified", entry.Postings[0].AccountName);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Given_UnvaluedPostings_When_Listed_Then_TheOldestComeFirstWithMoreFlagged()
    {
        // Act
        var reports = _fixture.CreateReports();
        var first = await reports.GetUnvaluedAsync(3, TestContext.Current.CancellationToken);
        var all = await reports.GetUnvaluedAsync(100, TestContext.Current.CancellationToken);

        // Assert: the forward's two lines, the payment's two and its adjustment's two
        Assert.Equal(3, first.Postings.Count);
        Assert.True(first.HasMore);
        Assert.Equal(6, all.Postings.Count);
        Assert.False(all.HasMore);
        Assert.Equal(6, all.Postings[0].Key.LedgerSeq);
        Assert.Equal((7L, 1), (all.Postings[^1].Key.LedgerSeq, all.Postings[^1].Key.Adjustment));
    }

    [Fact]
    public async Task Given_TheBooksOff_When_AReportIsAsked_Then_ItIsRefused()
    {
        // Arrange
        _fixture.Books.SetupGet(b => b.IsEnabled).Returns(false);

        // Act / Assert
        await Assert.ThrowsAsync<AccountingBooksDisabledException>(
            () => _fixture.CreateReports().GetIncomeStatementAsync(null, null, null,
                                                                  TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_TheFinancialBookOff_When_AReportIsAsked_Then_ItIsRefusedWithoutProjecting()
    {
        // Arrange
        _fixture.Projection.SetupGet(p => p.IsEnabled).Returns(false);

        // Act
        await Assert.ThrowsAsync<AccountingFinancialBooksDisabledException>(
            () => _fixture.CreateReports().GetBalanceSheetAsync(null, null, null, false,
                                                                TestContext.Current.CancellationToken));

        // Assert
        _fixture.Books.Verify(b => b.ProjectNowAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("U5D")]
    [InlineData("dollars")]
    public async Task Given_ABadCurrency_When_AReportIsAsked_Then_ItIsRefused(string currency)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _fixture.CreateReports().GetIncomeStatementAsync(null, null, currency,
                                                                  TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ASnapshot_When_TheRiskCapitalIsAsked_Then_EachStateIsWeightedAndTheRemoteLeftOut()
    {
        // Arrange: 600k sat local of which our 13k sat in flight (10k fulfilled), the peer's 400k with 11k in flight
        // (7k whose preimage we hold), 5k sat pending on chain, a wallet of 100k confirmed and 10k unconfirmed
        var channel = new ChannelBalanceBucket(new ChannelId(Enumerable.Repeat((byte)0x42, 32).ToArray()), null,
                                               ChannelState.Open, null, 1_000_000_000, 600_000_000, 400_000_000,
                                               13_000_000, 11_000_000, 5_000_000, 0, 1, true, 0, 10_000_000,
                                               7_000_000);
        var snapshot = new AccountingSnapshot(Now, 900_000, [channel],
                                              new WalletBalanceBucket(100_000_000, 10_000_000, 0));
        var source = new Mock<INodeSnapshotSource>();
        source.Setup(s => s.TakeSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        // Act
        var report = await _fixture.CreateReports(snapshotSource: source.Object)
                                   .GetRiskCapitalAsync(Usd, 100_000m, TestContext.Current.CancellationToken);

        // Assert
        var lines = report.Lines.ToDictionary(l => l.State);
        Assert.Equal(587_000_000, lines[AccountingRiskCapital.States.ChannelSettled].WeightedMsat);
        Assert.Equal(1_500_000, lines[AccountingRiskCapital.States.OutgoingInFlight].WeightedMsat);
        Assert.Equal(0, lines[AccountingRiskCapital.States.OutgoingFulfilled].WeightedMsat);
        Assert.Equal(6_930_000, lines[AccountingRiskCapital.States.IncomingWithPreimage].WeightedMsat);
        Assert.Equal(0, lines[AccountingRiskCapital.States.IncomingWithoutPreimage].WeightedMsat);
        Assert.Equal(4_750_000, lines[AccountingRiskCapital.States.PendingOnchain].WeightedMsat);
        Assert.Equal(9_000_000, lines[AccountingRiskCapital.States.WalletUnconfirmed].WeightedMsat);
        Assert.Equal(100_000_000 + 9_000_000 + 587_000_000 + 1_500_000 + 6_930_000 + 4_750_000, report.WeightedMsat);
        Assert.Equal(389_000_000, report.ExcludedRemoteMsat);
        Assert.Equal(709.18m, report.WeightedFiat);
        var bucket = Assert.Single(report.Channels);
        Assert.Equal(587_000_000 + 1_500_000 + 6_930_000 + 4_750_000, bucket.WeightedMsat);
        Assert.Equal(4_000_000, bucket.IncomingWithoutPreimageMsat);
        _fixture.Books.Verify(b => b.ProjectNowAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_NoSnapshotSource_When_TheRiskCapitalIsAsked_Then_ItIsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreateReports().GetRiskCapitalAsync(null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_NoProjector_When_AReportIsAsked_Then_TheStoredBookIsRead()
    {
        // Act
        var sheet = await _fixture.CreateReports(withProjection: false)
                                  .GetBalanceSheetAsync(null, null, null, false, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sheet.IsBalanced);
        _fixture.Projection.Verify(p => p.ProjectAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}