using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// Migration <c>AddAccountingFinancial</c> (NL-602 A3-T0) on SQLite: A2's books move into the operational book, the new
/// tables round-trip (the same assertions as the Docker Postgres/SQL Server tests), and the repositories keep the two
/// books apart and see what their unit of work staged.
/// </summary>
public class AccountingFinancialPersistenceTests
{
    private static readonly DateTimeOffset s_at = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_BooksFromBeforeAddAccountingFinancial_When_MigratedAndRolledBack_Then_TheyKeepTheirValues()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await AccountingFinancialSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_BooksFromBeforeAddAccountingFinancial_When_Migrated_Then_TheRebuiltTablesKeepTheirDefaults()
    {
        // Arrange: NL-134's lesson: the hand-written SQLite rebuild keeps the defaults the other providers have, so a
        // row written without the new columns is the operational book's
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var sql = new MigrationSqlDialect(DatabaseType.Sqlite);

        // Act
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingEntries", ("LedgerSeq", "7"), ("EventKey", "'k7'"), ("Kind", "1"),
                       ("OccurredAt", $"{s_at.UtcTicks}")), TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingPostings", ("LedgerSeq", "7"), ("Index", "0"), ("Account", "1"),
                       ("AmountMsat", "0"), ("OccurredAt", $"{s_at.UtcTicks}")), TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingBalances", ("Account", "1"), ("BalanceMsat", "0")),
            TestContext.Current.CancellationToken);

        // Assert
        var entry = await new AccountingBooksDbRepository(context).GetEntryByKeyAsync(
                        "k7", TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(AccountingBook.Operational, entry.Book);
        Assert.Equal(AccountingEntryFlags.None, entry.Flags);
        Assert.Single(entry.Postings);
        var balance = Assert.Single(await new AccountingBooksDbRepository(context)
                                       .GetAccountBalancesAsync(AccountingBook.Operational,
                                                                TestContext.Current.CancellationToken));
        Assert.Null(balance.AccountName);
        Assert.Equal(0m, balance.FiatAmount);
    }

    [Fact]
    public async Task Given_AMigratedDatabase_When_TheFinancialTablesAreWrittenAndReloaded_Then_EveryFieldRoundTrips()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        await using (var context = new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            // The financial round trip names the operational entry of ledger sequence 1
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(new AccountingEntry(1, "inv:a2:settled", AccountingEventKind.InvoiceSettled,
                                                          s_at, null, null,
                                                          [
                                                              new AccountingPosting(AccountRole.Channels, 5_000),
                                                              new AccountingPosting(AccountRole.Received, -5_000)
                                                          ]), TestContext.Current.CancellationToken);
            await books.SetCursorAsync(1, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act & Assert
        await AccountingFinancialSchemaRoundTrip.AssertFinancialTablesRoundTripAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_BothBooks_When_OneIsCleared_Then_TheOtherIsKept()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(Entry(1, AccountingBook.Operational), TestContext.Current.CancellationToken);
            await books.AddEntryAsync(Entry(1, AccountingBook.Financial), TestContext.Current.CancellationToken);
            await books.SetCursorAsync(AccountingBook.Operational, 1, TestContext.Current.CancellationToken);
            await books.SetCursorAsync(AccountingBook.Financial, 1, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using (var context = database.CreateContext())
            await new AccountingBooksDbRepository(context).ClearAsync(AccountingBook.Financial,
                                                                      TestContext.Current.CancellationToken);

        // Assert: the operational book is whole, the financial one is empty
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            Assert.Equal(1, await books.GetCursorAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await books.GetCursorAsync(AccountingBook.Financial,
                                                       TestContext.Current.CancellationToken));
            Assert.NotNull(await books.GetEntryByKeyAsync("k", TestContext.Current.CancellationToken));
            Assert.Empty(await books.GetEntriesByKeyAsync(AccountingBook.Financial, "k",
                                                          TestContext.Current.CancellationToken));
            Assert.Equal(2, (await books.GetBalancesAsync(TestContext.Current.CancellationToken)).Count);
            Assert.Empty(await books.GetAccountBalancesAsync(AccountingBook.Financial,
                                                             TestContext.Current.CancellationToken));
        }

        // Act: the operational clear (A2's rebuild) leaves a financial book alone too
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(Entry(1, AccountingBook.Financial), TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await books.ClearAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            Assert.Null(await books.GetEntryByKeyAsync("k", TestContext.Current.CancellationToken));
            Assert.Single(await books.GetEntriesByKeyAsync(AccountingBook.Financial, "k",
                                                           TestContext.Current.CancellationToken));
            Assert.Single(await books.ListEntriesAsync(new AccountingEntryQuery(0, 10) { Book = AccountingBook.Financial },
                                                       TestContext.Current.CancellationToken));
            Assert.Empty(await books.ListEntriesAsync(new AccountingEntryQuery(0, 10),
                                                      TestContext.Current.CancellationToken));
            Assert.Empty(await books.SumPostingsAsync(null, null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Given_AdjustmentsOfAFinancialEntry_When_PagedAfterOne_Then_ThePageStartsAfterIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var books = new AccountingBooksDbRepository(context);
        await books.AddEntryAsync(Entry(1, AccountingBook.Financial), TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(1, AccountingBook.Financial) with
        {
            Adjustment = 1,
            Flags = AccountingEntryFlags.Adjustment
        }, TestContext.Current.CancellationToken);
        await books.AddEntryAsync(Entry(2, AccountingBook.Financial, "k2"), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var afterFirst = await books.ListEntriesAsync(new AccountingEntryQuery(1, 10)
        {
            Book = AccountingBook.Financial,
            AfterAdjustment = 0
        }, TestContext.Current.CancellationToken);
        var afterSequence = await books.ListEntriesAsync(new AccountingEntryQuery(1, 10)
        {
            Book = AccountingBook.Financial
        }, TestContext.Current.CancellationToken);
        var adjustments = await books.ListEntriesAsync(new AccountingEntryQuery(0, 10)
        {
            Book = AccountingBook.Financial,
            WithFlags = AccountingEntryFlags.Adjustment
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([(1L, 1), (2L, 0)], afterFirst.Select(e => (e.LedgerSeq, e.Adjustment)));
        Assert.Equal([(2L, 0)], afterSequence.Select(e => (e.LedgerSeq, e.Adjustment)));
        Assert.Equal([(1L, 1)], adjustments.Select(e => (e.LedgerSeq, e.Adjustment)));
    }

    [Fact]
    public async Task Given_LotsStagedAndChanged_When_ListedInTheSameUnitOfWork_Then_TheStagedStateWins()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        long savedId;
        await using (var context = database.CreateContext())
        {
            savedId = await new AccountingLotDbRepository(context).AddLotAsync(Lot(s_at, 1_000),
                                                                               TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = database.CreateContext())
        {
            var lots = new AccountingLotDbRepository(context);

            // Act: a new lot gets the next id, the saved one is used up
            var stagedId = await lots.AddLotAsync(Lot(s_at.AddHours(-1), 2_000),
                                                  TestContext.Current.CancellationToken);
            var saved = await lots.GetLotAsync(savedId, TestContext.Current.CancellationToken);
            await lots.UpdateLotAsync(saved! with { RemainingMsat = 0 }, TestContext.Current.CancellationToken);
            var open = await lots.ListOpenLotsAsync(cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(savedId + 1, stagedId);
            Assert.Equal([stagedId], open.Select(l => l.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => lots.AddLotAsync(Lot(s_at, 1) with { Id = stagedId }, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => lots.AddLotAsync(Lot(s_at, 1) with { RemainingMsat = 2 }, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Given_AnOverrideAndARule_When_ReplacedDisabledAndRemoved_Then_TheChangesAreSaved()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        long ruleId;
        await using (var context = database.CreateContext())
        {
            await new AccountingOverrideDbRepository(context).SetAsync(
                new AccountingOverride("k", "income:sales", null, s_at), TestContext.Current.CancellationToken);
            new AccountingRuleDbRepository(context).Add(new AccountingRule(0, 1, null, null, null, null, null, null,
                                                                           null, "income:other", true, s_at));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            ruleId = (await new AccountingRuleDbRepository(context).ListAsync(false,
                                                                             TestContext.Current.CancellationToken))
                    .Single().Id;
        }

        // Act
        await using (var context = database.CreateContext())
        {
            var overrides = new AccountingOverrideDbRepository(context);
            await overrides.SetAsync(new AccountingOverride("k", "income:consulting", "moved", s_at.AddDays(1)),
                                     TestContext.Current.CancellationToken);
            Assert.Equal("income:consulting",
                         (await overrides.GetAsync("k", TestContext.Current.CancellationToken))!.Account);
            var rules = new AccountingRuleDbRepository(context);
            Assert.True(await rules.SetEnabledAsync(ruleId, false, TestContext.Current.CancellationToken));
            Assert.False(await rules.SetEnabledAsync(ruleId + 1, false, TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using (var context = database.CreateContext())
        {
            var overrides = new AccountingOverrideDbRepository(context);
            var replaced = await overrides.GetManyAsync(["k", "missing"], TestContext.Current.CancellationToken);
            Assert.Equal(new AccountingOverride("k", "income:consulting", "moved", s_at.AddDays(1)),
                         Assert.Single(replaced).Value);
            Assert.Single(await overrides.ListAsync(0, 10, TestContext.Current.CancellationToken));
            Assert.False((await new AccountingRuleDbRepository(context).GetByIdAsync(
                             ruleId, TestContext.Current.CancellationToken))!.Enabled);

            Assert.True(await overrides.RemoveAsync("k", TestContext.Current.CancellationToken));
            Assert.True(await new AccountingRuleDbRepository(context).RemoveAsync(
                            ruleId, TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = database.CreateContext())
        {
            Assert.Null(await new AccountingOverrideDbRepository(context).GetAsync(
                            "k", TestContext.Current.CancellationToken));
            Assert.Empty(await new AccountingRuleDbRepository(context).ListAsync(
                             false, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Given_InvalidRows_When_Staged_Then_TheRepositoriesRefuseThem()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var periods = new AccountingPeriodDbRepository(context);
        var period = new AccountingPeriod("2026-09", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                                          new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                                          AccountingPeriodState.Open, null, 0, null, null, null, false, null);
        await periods.AddAsync(period, TestContext.Current.CancellationToken);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => periods.AddAsync(period, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => periods.UpdateAsync(period with { PeriodId = "2026-10" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => periods.AddAsync(period with { PeriodId = "2026-11", End = period.Start },
                                   TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => new AccountingPriceDbRepository(context).TryAddAsync(
                new AccountingPrice(0, "US", s_at, 1m, AccountingPriceSource.Csv, s_at),
                TestContext.Current.CancellationToken));
        Assert.Equal("2026-09", (await periods.GetAsync("2026-09", TestContext.Current.CancellationToken))!.PeriodId);
    }

    [Fact]
    public async Task Given_LinesThatBreakTheBooksContract_When_Staged_Then_TheRepositoryRefusesThem()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var books = new AccountingBooksDbRepository(context);
        var financial = Entry(1, AccountingBook.Financial);
        var operational = Entry(1, AccountingBook.Operational);

        // Act & Assert: a financial line names its account, an operational one never does, a fiat amount has its
        // currency
        await Assert.ThrowsAsync<ArgumentException>(() => books.AddEntryAsync(
            financial with
            {
                Postings = [financial.Postings[0] with { AccountName = null }, financial.Postings[1]]
            }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => books.AddEntryAsync(
            operational with
            {
                Postings = [operational.Postings[0] with { AccountName = "assets:x" }, operational.Postings[1]]
            }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => books.AddEntryAsync(
            financial with
            {
                Postings = [financial.Postings[0] with { FiatAmount = 1m }, financial.Postings[1]]
            }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => books.AddEntryAsync(
            financial with
            {
                Postings =
                [
                    financial.Postings[0] with { FiatAmount = 1m, FiatCurrency = "US" }, financial.Postings[1]
                ]
            }, TestContext.Current.CancellationToken));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private static AccountingEntry Entry(long ledgerSeq, AccountingBook book, string key = "k") =>
        new(ledgerSeq, key, AccountingEventKind.InvoiceSettled, s_at, null, null,
            [
                new AccountingPosting(AccountRole.Channels, 1_000)
                {
                    AccountName = book == AccountingBook.Financial ? "assets:lightning:channels" : null
                },
                new AccountingPosting(AccountRole.Received, -1_000)
                {
                    AccountName = book == AccountingBook.Financial ? "income:sales" : null
                }
            ])
        { Book = book };

    private static AccountingLot Lot(DateTimeOffset acquiredAt, long msat) =>
        new(0, acquiredAt, AccountingLotOrigin.Acquisition, null, 0, null, null, msat, msat, null, null, null, false,
            null);
}