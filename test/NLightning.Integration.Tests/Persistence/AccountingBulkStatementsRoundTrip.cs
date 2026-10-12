using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// Provider-agnostic proof of the financial book's bulk statements (NL-662, NL-671), shared by the SQLite tests and the
/// Docker Postgres/SQL Server tests: <see cref="AccountingBooksDbRepository.ResetToCloseAsync"/> and
/// <see cref="AccountingBooksDbRepository.RollbackOpenEntriesAsync"/> run in a database transaction of their own and
/// use correlated <c>ExecuteDelete</c> (WHERE EXISTS over the lots to delete and the entries to reset), an
/// <c>ExecuteUpdate</c> of <c>RemainingMsat + n</c>, a <c>List&lt;long&gt;.Contains</c> over the open late facts'
/// ledger sequences and a bitwise flag test. Each seed holds an open late fact after the replay point (it goes with its
/// lots and reliefs, the reliefs given back) and one before it (kept like any adjustment), next to closed entries and
/// lots, imported lots and a plain adjustment with its lot.
/// </summary>
internal static class AccountingBulkStatementsRoundTrip
{
    private const string Period = "2026-09";
    private const AccountingEntryFlags LateFact = AccountingEntryFlags.Adjustment | AccountingEntryFlags.LateFact;

    private static readonly DateTimeOffset s_sep = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_oct = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_lateOct = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The reset to the last close (cursor 1, closing balance 1,000 msat / 0.6 on fin:channels).
    /// Seed: 1/0 (September, closed), 1/1 (a late fact before the replay point, 7 msat, lot 6, relieves 3 of lot 2),
    /// 2/0 (October), 2/1 (an adjustment, 40 msat, valued 0.1, lot 4, relieves 10 of lot 4 and 5 of lot 3), 3/1 (a late
    /// fact after the replay point, 20 msat, lot 5, relieves 25 of lot 1 and 15 of lot 4); cursor 3. Lots 1 (closed,
    /// 1,000), 2 (imported, 500), 3 (by 2/0, 300), 4 (by 2/1, 50), 5 (by 3/1, 60), 6 (by 1/1, 7); 2/0 relieves 200 of
    /// lot 1, 50 of lot 2 and 30 of lot 3; 1/0 relieved 100 of lot 1 (closed). Operational book: entry 1.
    /// </summary>
    public static async Task AssertResetToCloseAsync(Func<NLightningDbContext> contextFactory,
                                                     CancellationToken cancellationToken)
    {
        // Arrange
        await using (var context = contextFactory())
        {
            await context.Database.MigrateAsync(cancellationToken);
            var books = new AccountingBooksDbRepository(context);
            var lots = new AccountingLotDbRepository(context);
            await books.AddEntryAsync(Entry(1, 0, s_sep, AccountingBook.Operational), cancellationToken);
            await books.AddEntryAsync(Entry(1, 0, s_sep) with { ClosedPeriodId = Period }, cancellationToken);
            await books.AddEntryAsync(Entry(1, 1, s_oct, msat: 7) with { Flags = LateFact }, cancellationToken);
            await books.AddEntryAsync(Entry(2, 0, s_oct), cancellationToken);
            await books.AddEntryAsync(Entry(2, 1, s_oct, msat: 40, fiat: 0.1m) with
            {
                Flags = AccountingEntryFlags.Adjustment
            }, cancellationToken);
            await books.AddEntryAsync(Entry(3, 1, s_lateOct, msat: 20, fiat: 0.05m) with { Flags = LateFact },
                                      cancellationToken);
            await books.SetCursorAsync(AccountingBook.Financial, 3, cancellationToken);

            await lots.AddLotAsync(Lot(1, s_sep, AccountingLotOrigin.Acquisition, 1, 0, 1_000, 675, Period),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(2, s_sep, AccountingLotOrigin.Import, null, 0, 500, 447, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(3, s_oct, AccountingLotOrigin.Acquisition, 2, 0, 300, 265, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(4, s_oct, AccountingLotOrigin.Acquisition, 2, 1, 50, 25, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(5, s_lateOct, AccountingLotOrigin.Acquisition, 3, 1, 60, 60, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(6, s_oct, AccountingLotOrigin.Acquisition, 1, 1, 7, 7, null),
                                   cancellationToken);
            lots.AddRelief(new AccountingLotRelief(0, 1, 1, 0, s_sep, 100, null, null, Period));
            lots.AddRelief(new AccountingLotRelief(0, 2, 1, 1, s_oct, 3, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 1, 2, 0, s_oct, 200, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 2, 2, 0, s_oct, 50, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 3, 2, 0, s_oct, 30, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 4, 2, 1, s_oct, 10, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 3, 2, 1, s_oct, 5, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 1, 3, 1, s_lateOct, 25, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 4, 3, 1, s_lateOct, 15, null, null, null));
            await context.SaveChangesAsync(cancellationToken);
        }

        var closing = new AccountingAccountBalance(AccountingBook.Financial, AccountRole.Channels, "fin:channels",
                                                   1_000, 0.6m);

        // Act
        await using (var context = contextFactory())
            await new AccountingBooksDbRepository(context).ResetToCloseAsync(
                AccountingBook.Financial, new AccountingBookReset(1, [closing]), cancellationToken);

        // Assert: 2/0 and the late fact 3/1 gone; the closed 1/0, the late fact 1/1 (before the replay point) and the
        // adjustment 2/1 kept, with their postings
        await using var check = contextFactory();
        var after = new AccountingBooksDbRepository(check);
        var afterLots = new AccountingLotDbRepository(check);
        var entries = await after.ListEntriesAsync(new AccountingEntryQuery(0, 100)
        {
            Book = AccountingBook.Financial,
            AfterAdjustment = -1
        }, cancellationToken);
        Assert.Equal([(1L, 0), (1L, 1), (2L, 1)], entries.Select(e => (e.LedgerSeq, e.Adjustment)));
        Assert.All(entries, e => Assert.Equal(2, e.Postings.Count));
        Assert.Equal(1, await after.GetCursorAsync(AccountingBook.Financial, cancellationToken));
        Assert.Equal(1, await after.GetLastOpenEntrySeqAsync(AccountingBook.Financial, cancellationToken));

        // Balances: the closing balance plus the kept adjustments (2/1: 40 msat and 0.1, 1/1: 7 msat, unvalued)
        var balances = await after.GetAccountBalancesAsync(AccountingBook.Financial, cancellationToken);
        var channels = Assert.Single(balances, b => b.AccountName == "fin:channels");
        var received = Assert.Single(balances, b => b.AccountName == "fin:received");
        Assert.Equal((1_047L, 0.7m), (channels.BalanceMsat, channels.FiatAmount));
        Assert.Equal((-47L, -0.1m), (received.BalanceMsat, received.FiatAmount));

        // Lots: 3 (by 2/0) and 5 (by the late fact 3/1) gone, with the 5 msat 2/1 relieved of lot 3; lot 1 gets 200 + 25
        // back, lot 2 50, lot 4 15; lot 6 (by the late fact 1/1) untouched
        var kept = await afterLots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10, cancellationToken);
        Assert.Equal([1L, 2L, 4L, 6L], kept.Select(l => l.Id));
        Assert.Equal([900L, 497L, 40L, 7L], kept.Select(l => l.RemainingMsat));
        Assert.Equal([100L], (await afterLots.ListReliefsByLotAsync(1, cancellationToken)).Select(r => r.Msat));
        Assert.Equal([3L], (await afterLots.ListReliefsByLotAsync(2, cancellationToken)).Select(r => r.Msat));
        Assert.Equal([10L], (await afterLots.ListReliefsByLotAsync(4, cancellationToken)).Select(r => r.Msat));
        Assert.Equal(10, Assert.Single(await afterLots.ListReliefsByEntryAsync(2, 1, cancellationToken)).Msat);
        Assert.Empty(await afterLots.ListReliefsByEntryAsync(2, 0, cancellationToken));
        Assert.Empty(await afterLots.ListReliefsByEntryAsync(3, 1, cancellationToken));

        // The operational book is untouched
        Assert.Single(await after.ListEntriesAsync(new AccountingEntryQuery(0, 100), cancellationToken));
    }

    /// <summary>
    /// The rollback of the open entries from ledger sequence 3.
    /// Seed: 1/0 (September, closed), 2/0 (valued 0.2), 2/1 (a late fact before the rollback point, 7 msat, relieves 4
    /// of lot 1), 3/0 (valued 0.3), 3/1 (an adjustment, 40 msat, lot 5, relieves 5 of lot 4), 4/1 (a late fact after
    /// it, 20 msat valued 0.05, lot 6, relieves 25 of lot 3, 10 of lot 2 and 8 of lot 4); cursor 4. Lots 1 (closed,
    /// 1,000), 2 (imported, 500), 3 (by 2/0, 300), 4 (by 3/0, 200), 5 (by 3/1, 40), 6 (by 4/1, 60); 1/0 relieved 100 of
    /// lot 1 (closed), 2/0 relieves 50 of lot 2, 3/0 50 of lot 1 and 30 of lot 3. Operational book: entry 1.
    /// </summary>
    public static async Task AssertRollbackAsync(Func<NLightningDbContext> contextFactory,
                                                 CancellationToken cancellationToken)
    {
        // Arrange
        long lastBefore;
        await using (var context = contextFactory())
        {
            await context.Database.MigrateAsync(cancellationToken);
            var books = new AccountingBooksDbRepository(context);
            var lots = new AccountingLotDbRepository(context);
            await books.AddEntryAsync(Entry(1, 0, s_sep, AccountingBook.Operational), cancellationToken);
            await books.AddEntryAsync(Entry(1, 0, s_sep) with { ClosedPeriodId = Period }, cancellationToken);
            await books.AddEntryAsync(Entry(2, 0, s_oct, fiat: 0.2m), cancellationToken);
            await books.AddEntryAsync(Entry(2, 1, s_oct, msat: 7) with { Flags = LateFact }, cancellationToken);
            await books.AddEntryAsync(Entry(3, 0, s_oct, fiat: 0.3m), cancellationToken);
            await books.AddEntryAsync(Entry(3, 1, s_oct, msat: 40) with
            {
                Flags = AccountingEntryFlags.Adjustment
            }, cancellationToken);
            await books.AddEntryAsync(Entry(4, 1, s_lateOct, msat: 20, fiat: 0.05m) with { Flags = LateFact },
                                      cancellationToken);
            await books.SetCursorAsync(AccountingBook.Financial, 4, cancellationToken);

            await lots.AddLotAsync(Lot(1, s_sep, AccountingLotOrigin.Acquisition, 1, 0, 1_000, 846, Period),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(2, s_sep, AccountingLotOrigin.Import, null, 0, 500, 440, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(3, s_oct, AccountingLotOrigin.Acquisition, 2, 0, 300, 245, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(4, s_oct, AccountingLotOrigin.Acquisition, 3, 0, 200, 187, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(5, s_oct, AccountingLotOrigin.Acquisition, 3, 1, 40, 40, null),
                                   cancellationToken);
            await lots.AddLotAsync(Lot(6, s_lateOct, AccountingLotOrigin.Acquisition, 4, 1, 60, 60, null),
                                   cancellationToken);
            lots.AddRelief(new AccountingLotRelief(0, 1, 1, 0, s_sep, 100, null, null, Period));
            lots.AddRelief(new AccountingLotRelief(0, 2, 2, 0, s_oct, 50, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 1, 2, 1, s_oct, 4, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 1, 3, 0, s_oct, 50, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 3, 3, 0, s_oct, 30, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 4, 3, 1, s_oct, 5, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 3, 4, 1, s_lateOct, 25, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 2, 4, 1, s_lateOct, 10, null, null, null));
            lots.AddRelief(new AccountingLotRelief(0, 4, 4, 1, s_lateOct, 8, null, null, null));
            await context.SaveChangesAsync(cancellationToken);
            lastBefore = await books.GetLastOpenEntrySeqAsync(AccountingBook.Financial, cancellationToken);
        }

        // Act
        await using (var context = contextFactory())
            await new AccountingBooksDbRepository(context).RollbackOpenEntriesAsync(AccountingBook.Financial, 3,
                                                                                     cancellationToken);

        // Assert: 3/0 and the late fact 4/1 gone; the closed 1/0, 2/0, the late fact 2/1 (before the rollback point)
        // and the adjustment 3/1 kept, with their postings
        await using var check = contextFactory();
        var after = new AccountingBooksDbRepository(check);
        var afterLots = new AccountingLotDbRepository(check);
        var entries = await after.ListEntriesAsync(new AccountingEntryQuery(0, 100)
        {
            Book = AccountingBook.Financial,
            AfterAdjustment = -1
        }, cancellationToken);
        Assert.Equal(4, lastBefore);
        Assert.Equal([(1L, 0), (2L, 0), (2L, 1), (3L, 1)], entries.Select(e => (e.LedgerSeq, e.Adjustment)));
        Assert.All(entries, e => Assert.Equal(2, e.Postings.Count));
        Assert.Equal(2, await after.GetCursorAsync(AccountingBook.Financial, cancellationToken));
        Assert.Equal(2, await after.GetLastOpenEntrySeqAsync(AccountingBook.Financial, cancellationToken));

        // Balances: 1,000 + 1,000 + 7 + 40 msat left on fin:channels; 3/0's 0.3 and 4/1's 0.05 taken out of the fiat
        var balances = await after.GetAccountBalancesAsync(AccountingBook.Financial, cancellationToken);
        var channels = Assert.Single(balances, b => b.AccountName == "fin:channels");
        var received = Assert.Single(balances, b => b.AccountName == "fin:received");
        Assert.Equal((2_047L, 0.2m), (channels.BalanceMsat, channels.FiatAmount));
        Assert.Equal((-2_047L, -0.2m), (received.BalanceMsat, received.FiatAmount));

        // Lots: 4 (by 3/0) and 6 (by the late fact 4/1) gone, with 3/1's and 4/1's reliefs of lot 4; lot 1 gets 3/0's
        // 50 back, lot 2 4/1's 10, lot 3 30 + 25; the imported lot 2 and the adjustment's lot 5 stay
        var kept = await afterLots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10, cancellationToken);
        Assert.Equal([1L, 2L, 3L, 5L], kept.Select(l => l.Id));
        Assert.Equal([896L, 450L, 300L, 40L], kept.Select(l => l.RemainingMsat));
        Assert.Equal([100L, 4L], (await afterLots.ListReliefsByLotAsync(1, cancellationToken)).Select(r => r.Msat));
        Assert.Equal([50L], (await afterLots.ListReliefsByLotAsync(2, cancellationToken)).Select(r => r.Msat));
        Assert.Empty(await afterLots.ListReliefsByLotAsync(3, cancellationToken));
        Assert.Empty(await afterLots.ListReliefsByEntryAsync(3, 0, cancellationToken));
        Assert.Empty(await afterLots.ListReliefsByEntryAsync(3, 1, cancellationToken));
        Assert.Empty(await afterLots.ListReliefsByEntryAsync(4, 1, cancellationToken));

        // The operational book is untouched
        Assert.Single(await after.ListEntriesAsync(new AccountingEntryQuery(0, 100), cancellationToken));
    }

    private static AccountingEntry Entry(long ledgerSeq, int adjustment, DateTimeOffset at,
                                         AccountingBook book = AccountingBook.Financial, long msat = 1_000,
                                         decimal? fiat = null) =>
        new(ledgerSeq, $"k{ledgerSeq}", AccountingEventKind.InvoiceSettled, at, null, null,
            [
                new AccountingPosting(AccountRole.Channels, msat)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:channels" : null,
                    FiatAmount = fiat,
                    FiatCurrency = fiat is null ? null : "USD"
                },
                new AccountingPosting(AccountRole.Received, -msat)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:received" : null,
                    FiatAmount = -fiat,
                    FiatCurrency = fiat is null ? null : "USD"
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