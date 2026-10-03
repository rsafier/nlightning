namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// The repository members of the financial projector (NL-602 A3-T4) on SQLite: the last open entry, the rollback of the
/// open entries from a ledger sequence (entries, postings and balances, the reliefs given back, the lots they opened
/// gone, closed entries, adjustments and imported lots kept, cursor set) in one transaction, the lots by origin, and the
/// entry flags on the back-valuation's work list.
/// </summary>
public class AccountingFinancialRollbackTests
{
    private const string Period = "2026-09";

    private static readonly DateTimeOffset s_sep = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_oct = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_OpenEntries_When_RolledBackFromOne_Then_ItAndEverythingAfterIsUndoneButAdjustmentsStay()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await SeedAsync(database);
        long lastBefore;
        await using (var context = database.CreateContext())
            lastBefore = await new AccountingBooksDbRepository(context).GetLastOpenEntrySeqAsync(
                             AccountingBook.Financial, TestContext.Current.CancellationToken);

        // Act
        await using (var context = database.CreateContext())
            await new AccountingBooksDbRepository(context).RollbackOpenEntriesAsync(
                AccountingBook.Financial, 3, TestContext.Current.CancellationToken);

        // Assert: entry 3/0 gone, the closed 1/0, the open 2/0 and the adjustment 3/1 kept
        await using var check = database.CreateContext();
        var books = new AccountingBooksDbRepository(check);
        var lots = new AccountingLotDbRepository(check);
        var entries = await books.ListEntriesAsync(new AccountingEntryQuery(0, 100)
        {
            Book = AccountingBook.Financial,
            AfterAdjustment = -1
        }, TestContext.Current.CancellationToken);
        Assert.Equal(3, lastBefore);
        Assert.Equal([(1L, 0), (2L, 0), (3L, 1)], entries.Select(e => (e.LedgerSeq, e.Adjustment)));
        Assert.Equal(2, await books.GetCursorAsync(AccountingBook.Financial, TestContext.Current.CancellationToken));
        Assert.Equal(2, await books.GetLastOpenEntrySeqAsync(AccountingBook.Financial,
                                                              TestContext.Current.CancellationToken));

        // Balances: 1,000 + 1,000 + 40 (the adjustment) msat left on fin:channels; the fiat of 3/0 (0.3) taken out
        var balances = await books.GetAccountBalancesAsync(AccountingBook.Financial,
                                                           TestContext.Current.CancellationToken);
        var channels = Assert.Single(balances, b => b.AccountName == "fin:channels");
        Assert.Equal((2_040L, 0.2m), (channels.BalanceMsat, channels.FiatAmount));

        // Lots: lot 4 (opened by 3/0) gone with the adjustment's relief of it; lots 1 and 3 get 3/0's reliefs back;
        // the imported lot 2 and the adjustment's lot 5 stay
        var kept = await lots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10,
                                                          TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 3L, 5L], kept.Select(l => l.Id));
        Assert.Equal([900L, 450L, 300L, 40L], kept.Select(l => l.RemainingMsat));
        Assert.Equal([100L], (await lots.ListReliefsByLotAsync(1, TestContext.Current.CancellationToken))
                            .Select(r => r.Msat));
        Assert.Empty(await lots.ListReliefsByEntryAsync(3, 0, TestContext.Current.CancellationToken));
        Assert.Empty(await lots.ListReliefsByEntryAsync(3, 1, TestContext.Current.CancellationToken));

        // The operational book is untouched
        Assert.Single(await books.ListEntriesAsync(new AccountingEntryQuery(0, 100),
                                                   TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ImportedLots_When_ListedAndDeletedByOrigin_Then_OnlyTheyGoWithTheirReliefs()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await SeedAsync(database);

        // Act
        IReadOnlyList<AccountingLot> imported;
        int deleted;
        await using (var context = database.CreateContext())
        {
            var lots = new AccountingLotDbRepository(context);
            imported = await lots.ListLotsByOriginAsync(AccountingLotOrigin.Import,
                                                        TestContext.Current.CancellationToken);
            deleted = await lots.DeleteLotsByOriginAsync(AccountingLotOrigin.Import,
                                                         TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(2L, Assert.Single(imported).Id);
        Assert.Equal(1, deleted);
        await using var check = database.CreateContext();
        var after = new AccountingLotDbRepository(check);
        Assert.Equal([1L, 3L, 4L, 5L],
                     (await after.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10,
                                                              TestContext.Current.CancellationToken))
                    .Select(l => l.Id));
        Assert.Empty(await after.ListReliefsByLotAsync(2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AnEntryWaitingForItsPrice_When_TheWorkListIsRead_Then_ItsPostingsCarryTheEntryFlags()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext())
        {
            await new AccountingBooksDbRepository(context).AddEntryAsync(
                Entry(1, 0, s_oct, valued: false) with
                {
                    Flags = AccountingEntryFlags.Unvalued | AccountingEntryFlags.PendingValuation
                }, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using var check = database.CreateContext();
        var postings = await new AccountingBooksDbRepository(check).ListUnvaluedPostingsAsync(
                           AccountingBook.Financial, null, 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, postings.Count);
        Assert.All(postings, p => Assert.True(p.EntryFlags.HasFlag(AccountingEntryFlags.PendingValuation)));
    }

    [Fact]
    public async Task Given_LateFactsAroundTheRollbackPoint_When_RolledBack_Then_TheSharedRoundTripHolds()
    {
        // Arrange (NL-662, NL-671: the SQLite run of the round trip the Docker Postgres/SQL Server tests share)
        using var database = new SqliteTestDatabase();

        // Act & Assert
        await AccountingBulkStatementsRoundTrip.AssertRollbackAsync(() => database.CreateContext(),
                                                                    TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Financial book: 1/0 (September, closed), 2/0 and 3/0 (October, open), 3/1 (an adjustment of 40 msat), cursor 3;
    /// lots 1 (closed, by 1/0, 1,000), 2 (imported, 500), 3 (by 2/0, 300), 4 (by 3/0, 200), 5 (by 3/1, 40); reliefs 100
    /// of lot 1 by 1/0 (closed), 50 of lot 2 by 2/0, 50 of lot 1 and 30 of lot 3 by 3/0, 5 of lot 4 by 3/1.
    /// </summary>
    private static async Task SeedAsync(SqliteTestDatabase database)
    {
        await using var context = database.CreateContext();
        var books = new AccountingBooksDbRepository(context);
        var lots = new AccountingLotDbRepository(context);
        await books.AddEntryAsync(Entry(1, 0, s_sep, valued: false, book: AccountingBook.Operational),
                                  TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(1, 0, s_sep, valued: false) with { ClosedPeriodId = Period },
                                  TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(2, 0, s_oct, valued: true, fiat: 0.2m), TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(3, 0, s_oct, valued: true, fiat: 0.3m), TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(3, 1, s_oct, valued: false, msat: 40) with
        {
            Flags = AccountingEntryFlags.Adjustment
        }, TestContext.Current.CancellationToken);
        await books.SetCursorAsync(AccountingBook.Financial, 3, TestContext.Current.CancellationToken);

        await lots.AddLotAsync(Lot(1, s_sep, AccountingLotOrigin.Acquisition, 1, 0, 1_000, 850, Period),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(2, s_sep, AccountingLotOrigin.Import, null, 0, 500, 450, null),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(3, s_oct, AccountingLotOrigin.Acquisition, 2, 0, 300, 270, null),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(4, s_oct, AccountingLotOrigin.Acquisition, 3, 0, 200, 195, null),
                               TestContext.Current.CancellationToken);
        await lots.AddLotAsync(Lot(5, s_oct, AccountingLotOrigin.Acquisition, 3, 1, 40, 40, null),
                               TestContext.Current.CancellationToken);
        lots.AddRelief(new AccountingLotRelief(0, 1, 1, 0, s_sep, 100, null, null, Period));
        lots.AddRelief(new AccountingLotRelief(0, 2, 2, 0, s_oct, 50, null, null, null));
        lots.AddRelief(new AccountingLotRelief(0, 1, 3, 0, s_oct, 50, null, null, null));
        lots.AddRelief(new AccountingLotRelief(0, 3, 3, 0, s_oct, 30, null, null, null));
        lots.AddRelief(new AccountingLotRelief(0, 4, 3, 1, s_oct, 5, null, null, null));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AccountingEntry Entry(long ledgerSeq, int adjustment, DateTimeOffset at, bool valued,
                                         long msat = 1_000, decimal fiat = 0.1m,
                                         AccountingBook book = AccountingBook.Financial) =>
        new(ledgerSeq, $"k{ledgerSeq}", AccountingEventKind.InvoiceSettled, at, null, null,
            [
                new AccountingPosting(AccountRole.Channels, msat)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:channels" : null,
                    FiatAmount = valued ? fiat : null,
                    FiatCurrency = valued ? "USD" : null
                },
                new AccountingPosting(AccountRole.Received, -msat)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:received" : null,
                    FiatAmount = valued ? -fiat : null,
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