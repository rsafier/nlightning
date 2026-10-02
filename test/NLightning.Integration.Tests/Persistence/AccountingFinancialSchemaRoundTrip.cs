using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddAccountingFinancial</c> (NL-602 A3-T0), shared by the SQLite test and
/// the Docker Postgres/SQL Server tests: the operational books written by A2 (entries, postings, balances and the
/// singleton cursor) move into book 0 with their values, invoices and payments from before have no label, and every new
/// table (prices, rules, overrides, lots and reliefs, periods) and the financial book's columns round-trip through the
/// repositories. The rollback drops the financial book's rows and gives the operational cursor its A2 id back, and the
/// migration applies again.
/// </summary>
internal static class AccountingFinancialSchemaRoundTrip
{
    private const string MigrationName = "_AddAccountingFinancial";

    private static readonly DateTimeOffset s_at = new(2026, 9, 14, 10, 30, 0, 123, TimeSpan.Zero);

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x44, 32).ToArray());
        var invoiceHash = new Hash(Enumerable.Repeat((byte)0x71, 32).ToArray());

        // Arrange: the schema right before AddAccountingFinancial, with A2's books and an invoice
        string previous;
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            previous = migrations[migrations.IndexOf(target) - 1];
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(previous, cancellationToken);
            await SeedA2BooksAsync(context, sql, channelId, invoiceHash, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: A2's rows are the operational book's, with every value
        await AssertOperationalBookAsync(contextFactory, channelId, cancellationToken);
        await using (var context = contextFactory())
        {
            var invoice = await new InvoiceDbRepository(context).GetByPaymentHashAsync(invoiceHash);
            Assert.NotNull(invoice);
            Assert.Null(invoice.Label);
            Assert.Null(invoice.Tags);
        }

        // Act & Assert: the financial book and the new tables round-trip
        await AssertFinancialTablesRoundTripAsync(contextFactory, cancellationToken);

        // Act: roll back, then apply again
        await using (var context = contextFactory())
            await context.GetService<IMigrator>().MigrateAsync(previous, cancellationToken);

        // Assert: the older schema holds the operational book only, its cursor at the A2 id
        await using (var context = contextFactory())
        {
            Assert.Equal(1L, await ScalarAsync(context, sql.Select("AccountingCursor", "LastLedgerSeq", ("Id", "1")),
                                               cancellationToken));
            Assert.Equal(1L, await ScalarAsync(context, sql.Count("AccountingCursor"), cancellationToken));
            Assert.Equal(1L, await ScalarAsync(context, sql.Select("AccountingEntries", "LedgerSeq",
                                                                   ("EventKey", "'inv:a2:settled'")),
                                               cancellationToken));
            Assert.Equal(1L, await ScalarAsync(context, sql.Count("AccountingEntries"), cancellationToken));
            Assert.Equal(2L, await ScalarAsync(context, sql.Count("AccountingPostings"), cancellationToken));
            Assert.Equal(2L, await ScalarAsync(context, sql.Count("AccountingBalances"), cancellationToken));

            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        await AssertOperationalBookAsync(contextFactory, channelId, cancellationToken);
        await using (var context = contextFactory())
        {
            var books = new AccountingBooksDbRepository(context);
            Assert.Equal(0, await books.GetCursorAsync(AccountingBook.Financial, cancellationToken));
            Assert.Empty(await books.GetAccountBalancesAsync(AccountingBook.Financial, cancellationToken));
        }
    }

    /// <summary>
    /// Every new table and the financial book's columns round-trip on a migrated database.
    /// </summary>
    public static async Task AssertFinancialTablesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                                 CancellationToken cancellationToken)
    {
        // Prices: one per currency and time; the id is assigned by the save
        var price = new AccountingPrice(0, "USD", s_at.AddHours(-1), 86_048.12345678m, AccountingPriceSource.Http,
                                        s_at);
        await using (var context = contextFactory())
        {
            var prices = new AccountingPriceDbRepository(context);
            Assert.True(await prices.TryAddAsync(price, cancellationToken));
            Assert.False(await prices.TryAddAsync(price with { Price = 1m }, cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
        }

        long priceId;
        await using (var context = contextFactory())
        {
            var prices = new AccountingPriceDbRepository(context);
            var stored = await prices.GetAtOrBeforeAsync("usd", s_at, TimeSpan.FromHours(26), cancellationToken);
            Assert.NotNull(stored);
            Assert.Equal(price with { Id = stored.Id }, stored);
            Assert.Equal(stored, await prices.GetByIdAsync(stored.Id, cancellationToken));
            Assert.Null(await prices.GetAtOrBeforeAsync("USD", s_at, TimeSpan.FromMinutes(59), cancellationToken));
            Assert.Null(await prices.GetAtOrBeforeAsync("EUR", s_at, TimeSpan.FromHours(26), cancellationToken));
            Assert.Single(await prices.ListAsync("USD", null, null, 10, cancellationToken));
            priceId = stored.Id;
        }

        // The financial book: an entry with named accounts, one line valued and one not, and a later adjustment
        var entry = new AccountingEntry(1, "inv:a2:settled", AccountingEventKind.InvoiceSettled, s_at, null, null,
                                        [
                                            new AccountingPosting(AccountRole.Channels, 5_000)
                                            {
                                                AccountName = "assets:lightning:channels",
                                                FiatAmount = 4.30240617m,
                                                FiatCurrency = "USD",
                                                PriceId = priceId
                                            },
                                            new AccountingPosting(AccountRole.Received, -5_000)
                                            {
                                                AccountName = "income:sales"
                                            }
                                        ], "financial")
        {
            Book = AccountingBook.Financial,
            Flags = AccountingEntryFlags.Unvalued,
            Classification = AccountingClassificationSource.Rule,
            RuleId = 1
        };
        var adjustment = entry with
        {
            Adjustment = 1,
            OccurredAt = s_at.AddDays(30),
            Flags = AccountingEntryFlags.Adjustment,
            Classification = AccountingClassificationSource.Override,
            RuleId = null,
            Postings =
            [
                new AccountingPosting(AccountRole.Received, 5_000) { AccountName = "income:sales" },
                new AccountingPosting(AccountRole.Received, -5_000) { AccountName = "income:consulting" }
            ]
        };
        await using (var context = contextFactory())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(entry, cancellationToken);
            await books.AddEntryAsync(adjustment, cancellationToken);
            await books.SetCursorAsync(AccountingBook.Financial, 1, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var books = new AccountingBooksDbRepository(context);
            var stored = await books.GetEntriesByKeyAsync(AccountingBook.Financial, entry.EventKey,
                                                          cancellationToken);
            Assert.Equal(2, stored.Count);
            AssertSameEntry(entry, stored[0]);
            AssertSameEntry(adjustment, stored[1]);
            Assert.Equal(1, await books.GetCursorAsync(AccountingBook.Financial, cancellationToken));

            // The operational book is not touched by the financial one
            Assert.Equal(1, await books.GetCursorAsync(cancellationToken));
            Assert.Equal(-5_000, (await books.GetEntryByKeyAsync(entry.EventKey, cancellationToken))!
                                .Postings.Single(p => p.Account == AccountRole.Received).AmountMsat);

            var balances = await books.GetAccountBalancesAsync(AccountingBook.Financial, cancellationToken);
            Assert.Equal(5_000, Balance(balances, "assets:lightning:channels").BalanceMsat);
            Assert.Equal(4.30240617m, Balance(balances, "assets:lightning:channels").FiatAmount);
            Assert.Equal(0, Balance(balances, "income:sales").BalanceMsat);
            Assert.Equal(-5_000, Balance(balances, "income:consulting").BalanceMsat);

            // The back-valuation's work list and fill
            var unvalued = Assert.Single(await books.ListUnvaluedPostingsAsync(AccountingBook.Financial, 10,
                                                                               cancellationToken),
                                         p => p.Key.Adjustment == 0);
            Assert.Equal(new AccountingPostingKey(AccountingBook.Financial, 1, 0, 1), unvalued.Key);
            Assert.True(await books.SetPostingValueAsync(unvalued.Key, -4.30240617m, "USD", priceId,
                                                         cancellationToken));
            Assert.False(await books.SetPostingValueAsync(unvalued.Key, -4.30240617m, "USD", priceId,
                                                          cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Rules, overrides, lots and reliefs, a period
        var rule = new AccountingRule(0, 10, [AccountingEventKind.InvoiceSettled, AccountingEventKind.PaymentSucceeded],
                                      "^coffee", "customer", "acme*",
                                      new CompactPubKey(Convert.FromHexString(
                                          "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")),
                                      new Hash(Enumerable.Repeat((byte)0x0F, 32).ToArray()),
                                      new ChannelId(Enumerable.Repeat((byte)0x0E, 32).ToArray()), "income:sales",
                                      true, s_at, "coffee sales");
        var minimalRule = new AccountingRule(0, 5, null, null, null, null, null, null, null, "expenses:payments",
                                             false, s_at);
        var accountingOverride = new AccountingOverride(entry.EventKey, "income:consulting", "a consulting job",
                                                        s_at.AddDays(30));
        var lot = new AccountingLot(0, s_at, AccountingLotOrigin.Acquisition, 1, 0, null, null, 5_000, 5_000,
                                    4.30240617m, "USD", priceId, false, null);
        var opening = new AccountingLot(0, s_at.AddDays(-1), AccountingLotOrigin.Opening, null, 0,
                                        AccountRole.Wallet, null, 10_000, 10_000, null, null, null, true, null);
        long lotId;
        long openingId;
        await using (var context = contextFactory())
        {
            var rules = new AccountingRuleDbRepository(context);
            rules.Add(rule);
            rules.Add(minimalRule);
            await new AccountingOverrideDbRepository(context).SetAsync(accountingOverride, cancellationToken);
            var lots = new AccountingLotDbRepository(context);
            lotId = await lots.AddLotAsync(lot, cancellationToken);
            openingId = await lots.AddLotAsync(opening, cancellationToken);
            lots.AddRelief(new AccountingLotRelief(0, lotId, 1, 1, s_at.AddDays(30), 2_000, 1.72096247m, 2.5m,
                                                   null));
            await new AccountingPeriodDbRepository(context).AddAsync(
                new AccountingPeriod("2026-09", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                                     new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                                     AccountingPeriodState.Open, null, 0, null, null, null, false, null),
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal(lotId + 1, openingId);
        var digest = Enumerable.Repeat((byte)0xD1, 32).ToArray();
        var chainHash = Enumerable.Repeat((byte)0xC1, 32).ToArray();
        var signature = Enumerable.Repeat((byte)0x5A, 64).ToArray();
        await using (var context = contextFactory())
        {
            var rules = await new AccountingRuleDbRepository(context).ListAsync(false, cancellationToken);
            Assert.Equal(2, rules.Count);
            Assert.Equal(minimalRule with { Id = rules[0].Id }, rules[0]);
            Assert.Equal(rule.Kinds, rules[1].Kinds);
            Assert.Equal(rule with { Id = rules[1].Id, Kinds = rules[1].Kinds }, rules[1]);
            Assert.Single(await new AccountingRuleDbRepository(context).ListAsync(true, cancellationToken));

            Assert.Equal(accountingOverride,
                         await new AccountingOverrideDbRepository(context).GetAsync(entry.EventKey,
                                                                                    cancellationToken));

            var lots = new AccountingLotDbRepository(context);
            Assert.Equal(lot with { Id = lotId }, await lots.GetLotAsync(lotId, cancellationToken));
            var open = await lots.ListOpenLotsAsync(cancellationToken: cancellationToken);
            Assert.Equal([openingId, lotId], open.Select(l => l.Id));
            Assert.Equal(opening with { Id = openingId }, open[0]);
            var relief = Assert.Single(await lots.ListReliefsByLotAsync(lotId, cancellationToken));
            Assert.Equal(new AccountingLotRelief(relief.Id, lotId, 1, 1, s_at.AddDays(30), 2_000, 1.72096247m, 2.5m,
                                                 null), relief);
            Assert.Equal(relief, Assert.Single(await lots.ListReliefsByEntryAsync(1, 1, cancellationToken)));

            // The period's close (A3-T5's fields)
            var periods = new AccountingPeriodDbRepository(context);
            var period = await periods.GetAsync("2026-09", cancellationToken);
            Assert.NotNull(period);
            await periods.UpdateAsync(period with
            {
                State = AccountingPeriodState.Closed,
                ClosedAt = s_at.AddDays(20),
                LastLedgerSeq = 1,
                ChainHash = chainHash,
                Digest = digest,
                Signature = signature,
                Forced = true,
                ClosingState = "{\"balances\":[]}"
            }, cancellationToken);
            Assert.Equal(1, await new AccountingBooksDbRepository(context)
                                .MarkEntriesClosedAsync(AccountingBook.Financial, "2026-09", period.Start,
                                                        period.End, cancellationToken));
            Assert.Equal(2, await lots.MarkClosedAsync("2026-09", period.End, cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var periods = new AccountingPeriodDbRepository(context);
            var closed = await periods.GetLastClosedAsync(cancellationToken);
            Assert.NotNull(closed);
            Assert.Equal(AccountingPeriodState.Closed, closed.State);
            Assert.Equal(s_at.AddDays(20), closed.ClosedAt);
            Assert.Equal(chainHash, closed.ChainHash);
            Assert.Equal(digest, closed.Digest);
            Assert.Equal(signature, closed.Signature);
            Assert.True(closed.Forced);
            Assert.Equal("{\"balances\":[]}", closed.ClosingState);
            Assert.Equal("2026-09", (await periods.GetClosedContainingAsync(s_at, cancellationToken))?.PeriodId);
            Assert.Null(await periods.GetClosedContainingAsync(s_at.AddDays(30), cancellationToken));

            var books = new AccountingBooksDbRepository(context);
            var stored = await books.GetEntriesByKeyAsync(AccountingBook.Financial, entry.EventKey,
                                                          cancellationToken);
            Assert.Equal("2026-09", stored[0].ClosedPeriodId);
            Assert.Null(stored[1].ClosedPeriodId);
            Assert.Equal(-4.30240617m, stored[0].Postings[1].FiatAmount);
            Assert.Equal(priceId, stored[0].Postings[1].PriceId);
            var unvalued = await books.ListUnvaluedPostingsAsync(AccountingBook.Financial, 10, cancellationToken);
            Assert.Equal(2, unvalued.Count);
            Assert.All(unvalued, p => Assert.Equal(1, p.Key.Adjustment));
            var balances = await books.GetAccountBalancesAsync(AccountingBook.Financial, cancellationToken);
            Assert.Equal(-4.30240617m, Balance(balances, "income:sales").FiatAmount);

            var lots = new AccountingLotDbRepository(context);
            Assert.Equal("2026-09", (await lots.GetLotAsync(lotId, cancellationToken))!.ClosedPeriodId);
            Assert.Null(Assert.Single(await lots.ListReliefsByLotAsync(lotId, cancellationToken)).ClosedPeriodId);
        }
    }

    private static async Task AssertOperationalBookAsync(Func<NLightningDbContext> contextFactory,
                                                         ChannelId channelId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        var books = new AccountingBooksDbRepository(context);
        Assert.Equal(1, await books.GetCursorAsync(cancellationToken));
        Assert.Equal(1, await books.GetCursorAsync(AccountingBook.Operational, cancellationToken));

        var entry = await books.GetEntryByKeyAsync("inv:a2:settled", cancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(AccountingBook.Operational, entry.Book);
        Assert.Equal(0, entry.Adjustment);
        Assert.Equal(AccountingEntryFlags.None, entry.Flags);
        Assert.Null(entry.Classification);
        Assert.Null(entry.ClosedPeriodId);
        Assert.Equal(AccountingEventKind.InvoiceSettled, entry.Kind);
        Assert.Equal(s_at, entry.OccurredAt);
        Assert.Equal(channelId, entry.ChannelId);
        Assert.Equal("a2 row", entry.Note);
        Assert.Equal([new AccountingPosting(AccountRole.Channels, 5_000),
                      new AccountingPosting(AccountRole.Received, -5_000)], entry.Postings);

        var balances = await books.GetBalancesAsync(cancellationToken);
        Assert.Equal(5_000, balances[AccountRole.Channels]);
        Assert.Equal(-5_000, balances[AccountRole.Received]);
        Assert.Single(await books.ListEntriesAsync(new AccountingEntryQuery(0, 10), cancellationToken));
    }

    private static async Task SeedA2BooksAsync(NLightningDbContext context, MigrationSqlDialect sql,
                                               ChannelId channelId, Hash invoiceHash,
                                               CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingEntries",
                       ("LedgerSeq", "1"), ("EventKey", "'inv:a2:settled'"),
                       ("Kind", $"{(int)AccountingEventKind.InvoiceSettled}"), ("OccurredAt", $"{s_at.UtcTicks}"),
                       ("ChannelId", "{0}"), ("Note", "'a2 row'")),
            [(byte[])channelId], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingPostings",
                       ("LedgerSeq", "1"), ("Index", "0"), ("Account", $"{(int)AccountRole.Channels}"),
                       ("AmountMsat", "5000"), ("OccurredAt", $"{s_at.UtcTicks}")), cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingPostings",
                       ("LedgerSeq", "1"), ("Index", "1"), ("Account", $"{(int)AccountRole.Received}"),
                       ("AmountMsat", "-5000"), ("OccurredAt", $"{s_at.UtcTicks}")), cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingBalances", ("Account", $"{(int)AccountRole.Channels}"), ("BalanceMsat", "5000")),
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingBalances", ("Account", $"{(int)AccountRole.Received}"), ("BalanceMsat", "-5000")),
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("AccountingCursor", ("Id", "1"), ("LastLedgerSeq", "1")), cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Invoices",
                       ("PaymentHash", "{0}"), ("Preimage", "{1}"), ("PaymentSecret", "{2}"), ("Bolt11", "{3}"),
                       ("Kind", "0"), ("CreatedAt", $"{s_at.UtcTicks}"), ("ExpirySeconds", "3600"),
                       ("MinFinalCltvExpiry", "18"), ("Status", "0")),
            [(byte[])invoiceHash, Enumerable.Repeat((byte)0x72, 32).ToArray(),
             Enumerable.Repeat((byte)0x73, 32).ToArray(), "lnbcrt1a2invoice"], cancellationToken);
    }

    private static AccountingAccountBalance Balance(IEnumerable<AccountingAccountBalance> balances, string name) =>
        balances.Single(b => b.AccountName == name);

    private static void AssertSameEntry(AccountingEntry expected, AccountingEntry actual)
    {
        Assert.Equal(expected.Book, actual.Book);
        Assert.Equal(expected.LedgerSeq, actual.LedgerSeq);
        Assert.Equal(expected.Adjustment, actual.Adjustment);
        Assert.Equal(expected.EventKey, actual.EventKey);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.OccurredAt, actual.OccurredAt);
        Assert.Equal(expected.Note, actual.Note);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Classification, actual.Classification);
        Assert.Equal(expected.RuleId, actual.RuleId);
        Assert.Equal(expected.ClosedPeriodId, actual.ClosedPeriodId);
        Assert.Equal(expected.Postings, actual.Postings);
    }

    private static async Task<long> ScalarAsync(NLightningDbContext context, string query,
                                                CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = query;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}