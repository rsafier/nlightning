namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// The books' repository members the back-valuation uses (NL-602 A3-T2) on SQLite: the unvalued work list paged by a
/// cursor in (time, ledger sequence, adjustment, line) order, and an entry losing its <c>Unvalued</c> flag only once
/// every line of it is valued.
/// </summary>
public class AccountingValuationPersistenceTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_UnvaluedPostings_When_PagedByCursor_Then_EveryPostingComesOnceInOrder()
    {
        // Arrange: two entries at the same time (seq 2 then 3, the second with an adjustment), one earlier (seq 5)
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(Entry(3, s_at), TestContext.Current.CancellationToken);
            await books.AddEntryAsync(Entry(3, s_at, adjustment: 1), TestContext.Current.CancellationToken);
            await books.AddEntryAsync(Entry(2, s_at), TestContext.Current.CancellationToken);
            await books.AddEntryAsync(Entry(5, s_at.AddHours(-1)), TestContext.Current.CancellationToken);
            await books.AddEntryAsync(Entry(4, s_at, book: AccountingBook.Operational),
                                      TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var seen = new List<AccountingPostingKey>();
        AccountingUnvaluedPostingCursor? cursor = null;
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            while (true)
            {
                var page = await books.ListUnvaluedPostingsAsync(AccountingBook.Financial, cursor, 3,
                                                                 TestContext.Current.CancellationToken);
                seen.AddRange(page.Select(p => p.Key));
                if (page.Count < 3)
                    break;
                cursor = AccountingUnvaluedPostingCursor.After(page[^1]);
            }
        }

        // Assert
        Assert.Equal(
            [
                (5L, 0, 0), (5L, 0, 1), (2L, 0, 0), (2L, 0, 1), (3L, 0, 0), (3L, 0, 1), (3L, 1, 0), (3L, 1, 1)
            ], seen.Select(k => (k.LedgerSeq, k.Adjustment, k.Index)));
        Assert.All(seen, k => Assert.Equal(AccountingBook.Financial, k.Book));
    }

    [Fact]
    public async Task Given_ACursorAtATime_When_Listed_Then_OnlyLaterPostingsCome()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(Entry(1, s_at.AddHours(-2)), TestContext.Current.CancellationToken);
            await books.AddEntryAsync(Entry(2, s_at), TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using var reading = database.CreateContext();
        var page = await new AccountingBooksDbRepository(reading).ListUnvaluedPostingsAsync(
                       AccountingBook.Financial, AccountingUnvaluedPostingCursor.StartOf(s_at), 10,
                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([2L, 2L], page.Select(p => p.Key.LedgerSeq));
    }

    [Fact]
    public async Task Given_AnUnvaluedEntry_When_ItsLinesAreValuedOneByOne_Then_ItLosesTheFlagWithTheLast()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        long priceId;
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(Entry(1, s_at), TestContext.Current.CancellationToken);
            await new AccountingPriceDbRepository(context).TryAddAsync(
                new AccountingPrice(0, "USD", s_at, 100_000m, AccountingPriceSource.Http, s_at),
                TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            priceId = (await new AccountingPriceDbRepository(context).GetAtOrBeforeAsync(
                           "USD", s_at, TimeSpan.Zero, TestContext.Current.CancellationToken))!.Id;
        }

        // Act: the first line in one save, the second (and an already valued first) in another
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            Assert.True(await books.SetPostingValueAsync(new AccountingPostingKey(AccountingBook.Financial, 1, 0, 0),
                                                         1m, "USD", priceId, TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var flagsAfterFirst = await ReadFlagsAsync(database);
        await using (var context = database.CreateContext())
        {
            var books = new AccountingBooksDbRepository(context);
            Assert.False(await books.SetPostingValueAsync(new AccountingPostingKey(AccountingBook.Financial, 1, 0, 0),
                                                          1m, "USD", priceId, TestContext.Current.CancellationToken));
            Assert.True(await books.SetPostingValueAsync(new AccountingPostingKey(AccountingBook.Financial, 1, 0, 1),
                                                         -1m, "USD", priceId, TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.True(flagsAfterFirst.HasFlag(AccountingEntryFlags.Unvalued));
        var flags = await ReadFlagsAsync(database);
        Assert.False(flags.HasFlag(AccountingEntryFlags.Unvalued));
        Assert.True(flags.HasFlag(AccountingEntryFlags.Unclassified));
    }

    private static async Task<AccountingEntryFlags> ReadFlagsAsync(SqliteTestDatabase database)
    {
        await using var context = database.CreateContext();
        var entries = await new AccountingBooksDbRepository(context).GetEntriesByKeyAsync(
                          AccountingBook.Financial, "k1", TestContext.Current.CancellationToken);
        return Assert.Single(entries).Flags;
    }

    private static AccountingEntry Entry(long seq, DateTimeOffset at, int adjustment = 0,
                                         AccountingBook book = AccountingBook.Financial)
    {
        var financial = book == AccountingBook.Financial;
        return new AccountingEntry(seq, $"k{seq}", AccountingEventKind.InvoiceSettled, at, null, null,
                                   [
                                       new AccountingPosting(AccountRole.Channels, 1_000)
                                       {
                                           AccountName = financial ? "assets:lightning" : null
                                       },
                                       new AccountingPosting(AccountRole.Received, -1_000)
                                       {
                                           AccountName = financial ? "income:sales" : null
                                       }
                                   ])
        {
            Book = book,
            Adjustment = adjustment,
            Flags = financial
                        ? AccountingEntryFlags.Unvalued | AccountingEntryFlags.Unclassified
                        : AccountingEntryFlags.None
        };
    }
}