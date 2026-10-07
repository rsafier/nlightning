using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>Interrupted clears must preserve the complete journal and cursor on each real provider.</summary>
internal static class AccountingClearRecoveryRoundTrip
{
    public static async Task AssertAsync(Func<IInterceptor[], NLightningDbContext> createContext,
                                          AccountingBook book, string failedTable)
    {
        // Arrange: crash after at least the posting delete, while the old cursor still exists.
        var ct = TestContext.Current.CancellationToken;
        await using (var context = createContext([]))
        {
            await context.Database.MigrateAsync(ct);
            var repository = new AccountingBooksDbRepository(context);
            foreach (var seededBook in new[] { AccountingBook.Operational, AccountingBook.Financial })
            {
                await repository.AddEntryAsync(new AccountingEntry(1, "income", AccountingEventKind.InvoiceSettled,
                                                                   new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), null, null,
                                                                   [
                                                                       new AccountingPosting(AccountRole.Channels, 10)
                                                                       { AccountName = seededBook == AccountingBook.Financial ? "assets:lightning" : null },
                                                                       new AccountingPosting(AccountRole.Received, -10)
                                                                       { AccountName = seededBook == AccountingBook.Financial ? "income:sales" : null }
                                                                   ])
                { Book = seededBook }, ct);
                await repository.SetCursorAsync(seededBook, 1, ct);
            }
            await context.SaveChangesAsync(ct);
        }

        // Act
        await using (var context = createContext([new FailDelete(failedTable)]))
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new AccountingBooksDbRepository(context).ClearAsync(book, ct));

        // Assert: journal, balances and cursor roll back together, including on a fresh context.
        await using (var check = createContext([]))
        {
            Assert.Equal(2, await check.AccountingEntries.CountAsync(ct));
            Assert.Equal(4, await check.AccountingPostings.CountAsync(ct));
            Assert.Equal(4, await check.AccountingBalances.CountAsync(ct));
            Assert.Equal(2, await check.AccountingCursor.CountAsync(ct));
            var repository = new AccountingBooksDbRepository(check);
            foreach (var seededBook in new[] { AccountingBook.Operational, AccountingBook.Financial })
                Assert.Equal(1, await repository.GetCursorAsync(seededBook, ct));
            await repository.ClearAsync(book, ct);
        }
        await using var after = createContext([]);
        Assert.Equal(1, await after.AccountingEntries.CountAsync(ct));
        Assert.Equal(2, await after.AccountingPostings.CountAsync(ct));
        Assert.Equal(2, await after.AccountingBalances.CountAsync(ct));
        var reader = new AccountingBooksDbRepository(after);
        Assert.Equal(0, await reader.GetCursorAsync(book, ct));
        Assert.Equal(1, await reader.GetCursorAsync(book == AccountingBook.Operational
                                                      ? AccountingBook.Financial : AccountingBook.Operational, ct));
    }

    private sealed class FailDelete(string table) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
             && (command.CommandText.Contains(table, StringComparison.Ordinal)
              || command.CommandText.Contains(SnakeCase(table), StringComparison.Ordinal)))
                throw new InvalidOperationException("Injected accounting clear failure");
            return ValueTask.FromResult(result);
        }
    }

    private static string SnakeCase(string name) =>
        name switch
        {
            "AccountingEntries" => "accounting_entries",
            "AccountingBalances" => "accounting_balances",
            "AccountingCursor" => "accounting_cursor",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
}