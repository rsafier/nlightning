using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// Migration <c>AddAccountingBooks</c> (NL-602 A2) on the real SQLite schema: entries round trip with their postings,
/// the running balances follow the entries in the same save, the cursor is a single row, staged entries are found by
/// key, period sums and filtered pages read the postings, and a clear empties the books.
/// </summary>
public class AccountingBooksPersistenceTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);

    [Fact]
    public async Task Given_AnEntryWithEveryField_When_SavedWithTheCursorAndReloaded_Then_EveryFieldIsEqual()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x33, 32).ToArray());
        var original = new AccountingEntry(1, "inv:aa:settled", AccountingEventKind.InvoiceSettled, s_at.AddTicks(7),
                                           channelId, Hash(0x11),
                                           [
                                               new AccountingPosting(AccountRole.Channels, 5_000),
                                               new AccountingPosting(AccountRole.Received, -5_000)
                                           ], "coffee");
        AccountingEntry? staged;
        await using (var context = database.CreateContext())
        {
            var repository = new AccountingBooksDbRepository(context);
            await repository.AddEntryAsync(original, TestContext.Current.CancellationToken);
            await repository.SetCursorAsync(1, TestContext.Current.CancellationToken);

            // Act (staged)
            staged = await repository.GetEntryByKeyAsync(original.EventKey, TestContext.Current.CancellationToken);
            Assert.Equal(1, await repository.GetCursorAsync(TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        AccountingEntry? reloaded;
        long cursor;
        IReadOnlyDictionary<AccountRole, long> balances;
        await using (var context = database.CreateContext())
        {
            var repository = new AccountingBooksDbRepository(context);
            reloaded = await repository.GetEntryByKeyAsync(original.EventKey, TestContext.Current.CancellationToken);
            cursor = await repository.GetCursorAsync(TestContext.Current.CancellationToken);
            balances = await repository.GetBalancesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        AssertSameEntry(original, staged);
        AssertSameEntry(original, reloaded);
        Assert.Equal(1, cursor);
        Assert.Equal(5_000, balances[AccountRole.Channels]);
        Assert.Equal(-5_000, balances[AccountRole.Received]);
        Assert.Equal(2, balances.Count);
    }

    [Fact]
    public async Task Given_EntriesInSeveralSaves_When_TheBalancesAreRead_Then_TheyAreTheRunningSums()
    {
        // Arrange: the second save adds to loaded balance rows, the third to rows tracked in its own unit of work
        using var database = new SqliteTestDatabase();
        await AddAsync(database, 1, Entry(1, "a", (AccountRole.Channels, 1_000), (AccountRole.Received, -1_000)));
        await AddAsync(database, 3,
                       Entry(2, "b", (AccountRole.Channels, -300), (AccountRole.Sent, 250),
                             (AccountRole.RoutingFees, 50)),
                       Entry(3, "c", (AccountRole.Channels, 20), (AccountRole.Routing, -20)));
        await AddAsync(database, 4, Entry(4, "memo"));

        // Act
        IReadOnlyDictionary<AccountRole, long> balances;
        long cursor;
        await using (var context = database.CreateContext())
        {
            var repository = new AccountingBooksDbRepository(context);
            balances = await repository.GetBalancesAsync(TestContext.Current.CancellationToken);
            cursor = await repository.GetCursorAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(720, balances[AccountRole.Channels]);
        Assert.Equal(-1_000, balances[AccountRole.Received]);
        Assert.Equal(250, balances[AccountRole.Sent]);
        Assert.Equal(50, balances[AccountRole.RoutingFees]);
        Assert.Equal(-20, balances[AccountRole.Routing]);
        Assert.Equal(0, balances.Values.Sum());
        Assert.Equal(4, cursor);
        await using var check = database.CreateContext();
        Assert.Equal(1, await check.AccountingCursor.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, await check.AccountingEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AnEntryThatDoesNotBalance_When_Added_Then_ItIsRefusedAndNothingIsStaged()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var repository = new AccountingBooksDbRepository(context);

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => repository.AddEntryAsync(
                                                                         Entry(1, "bad", (AccountRole.Channels, 10)),
                                                                         TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("bad", exception.Message, StringComparison.Ordinal);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Given_ASecondEntryWithTheSameKey_When_Saved_Then_TheUniqueIndexRefusesIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await AddAsync(database, 1, Entry(1, "k"));
        await using var context = database.CreateContext();
        await new AccountingBooksDbRepository(context).AddEntryAsync(Entry(2, "k"),
                                                                     TestContext.Current.CancellationToken);

        // Act / Assert
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(
                                                        TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_EntriesOverTime_When_SummedAndListed_Then_PeriodsAndFiltersApply()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var channelA = new ChannelId(Enumerable.Repeat((byte)0xA1, 32).ToArray());
        var channelB = new ChannelId(Enumerable.Repeat((byte)0xB2, 32).ToArray());
        await AddAsync(database, 3,
                       Entry(1, "e1", channelA, AccountingEventKind.InvoiceSettled, s_at,
                             (AccountRole.Channels, 100), (AccountRole.Received, -100)),
                       Entry(2, "e2", channelB, AccountingEventKind.ForwardSettled, s_at.AddHours(1),
                             (AccountRole.Channels, 7), (AccountRole.Routing, -7)),
                       Entry(3, "e3", channelA, AccountingEventKind.InvoiceSettled, s_at.AddHours(2),
                             (AccountRole.Channels, 50), (AccountRole.Received, -50)));

        // Act
        await using var context = database.CreateContext();
        var repository = new AccountingBooksDbRepository(context);
        var all = await repository.SumPostingsAsync(null, null, TestContext.Current.CancellationToken);
        var period = await repository.SumPostingsAsync(s_at.AddMinutes(30), s_at.AddHours(2),
                                                       TestContext.Current.CancellationToken);
        var invoices = await repository.ListEntriesAsync(
                           new AccountingEntryQuery(0, 10, Kinds: [AccountingEventKind.InvoiceSettled]),
                           TestContext.Current.CancellationToken);
        var routing = await repository.ListEntriesAsync(new AccountingEntryQuery(0, 10, Account: AccountRole.Routing),
                                                        TestContext.Current.CancellationToken);
        var channel = await repository.ListEntriesAsync(new AccountingEntryQuery(1, 10, ChannelId: channelA),
                                                        TestContext.Current.CancellationToken);
        var page = await repository.ListEntriesAsync(
                       new AccountingEntryQuery(0, 2, Since: s_at.AddMinutes(1), Until: s_at.AddHours(3)),
                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(157, all[AccountRole.Channels]);
        Assert.Equal(-150, all[AccountRole.Received]);
        Assert.Equal(7, period[AccountRole.Channels]);
        Assert.False(period.ContainsKey(AccountRole.Received));
        Assert.Equal(["e1", "e3"], invoices.Select(e => e.EventKey));
        Assert.Equal(2, invoices[1].Postings.Count);
        Assert.Equal(["e2"], routing.Select(e => e.EventKey));
        Assert.Equal(["e3"], channel.Select(e => e.EventKey));
        Assert.Equal(["e2", "e3"], page.Select(e => e.EventKey));
        Assert.Equal([AccountRole.Channels, AccountRole.Routing], page[0].Postings.Select(p => p.Account));
    }

    [Fact]
    public async Task Given_BooksWithEntries_When_Cleared_Then_EverythingIsGoneAndNewEntriesCanBeAdded()
    {
        // Arrange: a balance row tracked in the clearing unit of work too
        using var database = new SqliteTestDatabase();
        await AddAsync(database, 2, Entry(1, "a", (AccountRole.Channels, 10), (AccountRole.Received, -10)),
                       Entry(2, "b", (AccountRole.Wallet, 5), (AccountRole.TransfersIn, -5)));
        await using (var context = database.CreateContext())
        {
            var repository = new AccountingBooksDbRepository(context);
            await repository.AddEntryAsync(Entry(3, "c", (AccountRole.Channels, 1), (AccountRole.Received, -1)),
                                           TestContext.Current.CancellationToken);

            // Act
            await repository.ClearAsync(TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await repository.AddEntryAsync(Entry(1, "a", (AccountRole.Channels, 3), (AccountRole.Received, -3)),
                                           TestContext.Current.CancellationToken);
            await repository.SetCursorAsync(1, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var check = database.CreateContext();
        var reader = new AccountingBooksDbRepository(check);
        Assert.Equal(1, await reader.GetCursorAsync(TestContext.Current.CancellationToken));
        var balances = await reader.GetBalancesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, balances.Count);
        Assert.Equal(3, balances[AccountRole.Channels]);
        Assert.Equal(1, await check.AccountingEntries.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await check.AccountingPostings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Null(await reader.GetEntryByKeyAsync("b", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(AccountingBook.Operational, "AccountingEntries")]
    [InlineData(AccountingBook.Operational, "AccountingBalances")]
    [InlineData(AccountingBook.Operational, "AccountingCursor")]
    [InlineData(AccountingBook.Financial, "AccountingEntries")]
    [InlineData(AccountingBook.Financial, "AccountingBalances")]
    [InlineData(AccountingBook.Financial, "AccountingCursor")]
    public async Task Given_TwoBooks_When_AClearIsInterrupted_Then_BothBooksRemainIntactAndTheClearCanRetry(
        AccountingBook book, string failedTable)
    {
        using var database = new SqliteTestDatabase();
        await AccountingClearRecoveryRoundTrip.AssertAsync(database.CreateContext, book, failedTable);
    }

    private static async Task AddAsync(SqliteTestDatabase database, long cursor, params AccountingEntry[] entries)
    {
        await using var context = database.CreateContext();
        var repository = new AccountingBooksDbRepository(context);
        foreach (var entry in entries)
            await repository.AddEntryAsync(entry, TestContext.Current.CancellationToken);
        await repository.SetCursorAsync(cursor, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AccountingEntry Entry(long ledgerSeq, string key, params (AccountRole Account, long Amount)[] lines)
        => Entry(ledgerSeq, key, null, AccountingEventKind.InvoiceSettled, s_at, lines);

    private static AccountingEntry Entry(long ledgerSeq, string key, ChannelId? channelId, AccountingEventKind kind,
                                         DateTimeOffset at, params (AccountRole Account, long Amount)[] lines) =>
        new(ledgerSeq, key, kind, at, channelId, null,
            lines.Select(l => new AccountingPosting(l.Account, l.Amount)).ToList());

    private static void AssertSameEntry(AccountingEntry expected, AccountingEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.LedgerSeq, actual.LedgerSeq);
        Assert.Equal(expected.EventKey, actual.EventKey);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.OccurredAt, actual.OccurredAt);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.PaymentHash, actual.PaymentHash);
        Assert.Equal(expected.Note, actual.Note);
        Assert.Equal(expected.Postings, actual.Postings);
    }

    private static Hash Hash(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());
}