namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// The repository members of the period close (NL-602 A3-T5) on SQLite: the close's reads (unvalued postings of the
/// open period, the lots acquired before a time, a period's reliefs, the reliefs since a time), the lock's refusal to
/// value a closed posting, and the financial reset to the last close (open entries but adjustments gone, reliefs given
/// back, the open period's lots gone, imported and adjustment lots kept, balances and cursor set) in one transaction.
/// </summary>
public class AccountingPeriodPersistenceTests
{
    private const string Period = "2026-09";

    private static readonly DateTimeOffset s_sep = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_end = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_oct = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_ClosedAndOpenRows_When_TheCloseReadsThem_Then_EachReadSelectsItsRows()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await SeedAsync(database);

        // Act
        await using var context = database.CreateContext();
        var books = new AccountingBooksDbRepository(context);
        var lots = new AccountingLotDbRepository(context);
        var unvaluedBeforeEnd = await books.CountOpenUnvaluedPostingsAsync(AccountingBook.Financial, s_end,
                                                                           TestContext.Current.CancellationToken);
        var unvaluedAll = await books.CountOpenUnvaluedPostingsAsync(AccountingBook.Financial, DateTimeOffset.MaxValue,
                                                                     TestContext.Current.CancellationToken);
        var before = await lots.ListLotsAcquiredBeforeAsync(s_end, 0, 10, TestContext.Current.CancellationToken);
        var page = await lots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 1, 2,
                                                          TestContext.Current.CancellationToken);
        var closedReliefs = await lots.ListPeriodReliefsAsync(Period, s_end, 0, 10,
                                                              TestContext.Current.CancellationToken);
        var openReliefs = await lots.ListPeriodReliefsAsync(null, DateTimeOffset.MaxValue, 0, 10,
                                                            TestContext.Current.CancellationToken);
        var since = await lots.SumReliefsSinceAsync(s_end, TestContext.Current.CancellationToken);

        // Assert: the September entry is closed, so only October's unvalued lines count, and only without an end
        Assert.Equal(0, unvaluedBeforeEnd);
        Assert.Equal(2, unvaluedAll);
        Assert.Equal([1L, 2L], before.Select(l => l.Id));
        Assert.Equal([2L, 3L], page.Select(l => l.Id));
        Assert.Equal(100, Assert.Single(closedReliefs).Msat);
        Assert.Equal([200L, 50L, 30L, 10L], openReliefs.Select(r => r.Msat));
        Assert.Equal(new Dictionary<long, long> { [1] = 200, [2] = 50, [3] = 30, [4] = 10 }, since);
    }

    [Fact]
    public async Task Given_AClosedPosting_When_Valued_Then_TheLockRefusesIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await SeedAsync(database);

        // Act
        await using var context = database.CreateContext();
        var books = new AccountingBooksDbRepository(context);
        var closed = await books.SetPostingValueAsync(new AccountingPostingKey(AccountingBook.Financial, 1, 0, 0), 1m,
                                                      "USD", 1, TestContext.Current.CancellationToken);
        var open = await books.SetPostingValueAsync(new AccountingPostingKey(AccountingBook.Financial, 2, 0, 0), 1m,
                                                    "USD", 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(closed);
        Assert.True(open);
    }

    [Fact]
    public async Task Given_AnOpenPeriod_When_TheFinancialBookIsResetToTheClose_Then_OnlyTheClosedStateAndAdjustmentsStay()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await SeedAsync(database);
        var closing = new AccountingAccountBalance(AccountingBook.Financial, AccountRole.Channels, "fin:channels",
                                                   1_000, 0.6m);

        // Act
        await using (var context = database.CreateContext())
            await new AccountingBooksDbRepository(context).ResetToCloseAsync(
                AccountingBook.Financial, new AccountingBookReset(1, [closing]), TestContext.Current.CancellationToken);

        // Assert
        await using var check = database.CreateContext();
        var books = new AccountingBooksDbRepository(check);
        var lots = new AccountingLotDbRepository(check);
        var entries = await books.ListEntriesAsync(new AccountingEntryQuery(0, 100)
        {
            Book = AccountingBook.Financial,
            AfterAdjustment = -1
        }, TestContext.Current.CancellationToken);
        Assert.Equal([(1L, 0), (2L, 1)], entries.Select(e => (e.LedgerSeq, e.Adjustment)));
        Assert.Equal(1, await books.GetCursorAsync(AccountingBook.Financial, TestContext.Current.CancellationToken));

        // The closing balance plus the kept adjustment (+40 msat to fin:channels, -40 to fin:received)
        var balances = await books.GetAccountBalancesAsync(AccountingBook.Financial,
                                                           TestContext.Current.CancellationToken);
        Assert.Equal(1_040, Assert.Single(balances, b => b.AccountName == "fin:channels").BalanceMsat);
        Assert.Equal(-40, Assert.Single(balances, b => b.AccountName == "fin:received").BalanceMsat);

        // Lot 1 (closed) gets the open relief back, lot 2 (imported) too, lot 3 (open) is gone, lot 4 (adjustment)
        // stays; the closed relief and the adjustment's relief stay
        var kept = await lots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10,
                                                          TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 4L], kept.Select(l => l.Id));
        Assert.Equal(900, kept[0].RemainingMsat);
        Assert.Equal(500, kept[1].RemainingMsat);
        Assert.Equal(40, kept[2].RemainingMsat);
        Assert.Equal([100L], (await lots.ListReliefsByLotAsync(1, TestContext.Current.CancellationToken))
                            .Select(r => r.Msat));
        Assert.Empty(await lots.ListReliefsByLotAsync(2, TestContext.Current.CancellationToken));
        Assert.Single(await lots.ListReliefsByEntryAsync(2, 1, TestContext.Current.CancellationToken));

        // The operational book is untouched
        Assert.Single(await books.ListEntriesAsync(new AccountingEntryQuery(0, 100),
                                                   TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ALateFactAfterTheClose_When_TheFinancialBookIsResetToTheClose_Then_TheSharedRoundTripHolds()
    {
        // Arrange (NL-662, NL-671: the SQLite run of the round trip the Docker Postgres/SQL Server tests share)
        using var database = new SqliteTestDatabase();

        // Act & Assert
        await AccountingBulkStatementsRoundTrip.AssertResetToCloseAsync(() => database.CreateContext(),
                                                                        TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Financial book: entry 1/0 (September, closed), entry 2/0 (October, open, unvalued), entry 2/1 (an adjustment);
    /// lots 1 (closed, 1,000), 2 (imported, 500), 3 (opened by entry 2/0), 4 (opened by the adjustment, 40); reliefs
    /// 100 of lot 1 (closed), 200 of lot 1, 50 of lot 2 and 30 of lot 3 by entry 2/0, 10 of lot 4 by the adjustment.
    /// Operational book: entry 1.
    /// </summary>
    private static async Task SeedAsync(SqliteTestDatabase database)
    {
        await using var context = database.CreateContext();
        var books = new AccountingBooksDbRepository(context);
        var lots = new AccountingLotDbRepository(context);
        await books.AddEntryAsync(Entry(1, 0, s_sep, AccountingBook.Operational, valued: false),
                                  TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(1, 0, s_sep, AccountingBook.Financial, valued: false) with
        {
            ClosedPeriodId = Period
        }, TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(2, 0, s_oct, AccountingBook.Financial, valued: false),
                                  TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(2, 1, s_oct, AccountingBook.Financial, valued: true, msat: 40) with
        {
            Flags = AccountingEntryFlags.Adjustment
        }, TestContext.Current.CancellationToken);
        await books.SetCursorAsync(AccountingBook.Financial, 2, TestContext.Current.CancellationToken);

        await lots.AddLotAsync(Lot(1, s_sep, AccountingLotOrigin.Acquisition, 1, 0, 1_000, 700, Period),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(2, s_sep, AccountingLotOrigin.Import, null, 0, 500, 450, null),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(3, s_oct, AccountingLotOrigin.Acquisition, 2, 0, 300, 270, null),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(4, s_oct, AccountingLotOrigin.Acquisition, 2, 1, 50, 40, null),
                               TestContext.Current.CancellationToken);
        lots.AddRelief(new AccountingLotRelief(0, 1, 1, 0, s_sep, 100, null, null, Period));
        lots.AddRelief(new AccountingLotRelief(0, 1, 2, 0, s_oct, 200, null, null, null));
        lots.AddRelief(new AccountingLotRelief(0, 2, 2, 0, s_oct, 50, null, null, null));
        lots.AddRelief(new AccountingLotRelief(0, 3, 2, 0, s_oct, 30, null, null, null));
        lots.AddRelief(new AccountingLotRelief(0, 4, 2, 1, s_oct, 10, null, null, null));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AccountingEntry Entry(long ledgerSeq, int adjustment, DateTimeOffset at, AccountingBook book,
                                         bool valued, long msat = 1_000) =>
        new(ledgerSeq, $"k{ledgerSeq}", AccountingEventKind.InvoiceSettled, at, null, null,
            [
                new AccountingPosting(AccountRole.Channels, msat)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:channels" : null,
                    FiatAmount = valued ? 0.1m : null,
                    FiatCurrency = valued ? "USD" : null
                },
                new AccountingPosting(AccountRole.Received, -msat)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:received" : null,
                    FiatAmount = valued ? -0.1m : null,
                    FiatCurrency = valued ? "USD" : null
                }
            ])
        {
            Book = book,
            Adjustment = adjustment
        };

    private static AccountingLot Lot(long id, DateTimeOffset at, AccountingLotOrigin origin, long? source,
                                     int sourceAdjustment, long original, long remaining, string? period) =>
        new(id, at, origin, source, sourceAdjustment, null, null, original, remaining, null, null, null, false, period);
}