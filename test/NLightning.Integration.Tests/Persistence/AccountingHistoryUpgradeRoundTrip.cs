using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Enums;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>Historical sealed wallet events retain their hashes and gain indexed reversal references on upgrade.</summary>
internal static class AccountingHistoryUpgradeRoundTrip
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                        CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        await using var context = contextFactory();
        var previous = context.Database.GetMigrations().Single(m => m.EndsWith(
            "_IndexImportedHistoryAndRecoveryKeyState", StringComparison.Ordinal));
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, cancellationToken);
        var hash = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        long sequence = 0;
        async Task InsertAsync(string key, AccountingEventKind kind, string? details = null)
        {
            var seq = ++sequence;
            // Supply a typed parameter for SQL NULL; a bare DBNull has no EF CLR type mapping.
            using var dbCommand = context.Database.GetDbConnection().CreateCommand();
            var detailsParameter = dbCommand.CreateParameter();
            detailsParameter.ParameterName = "historyDetails";
            detailsParameter.DbType = DbType.String;
            detailsParameter.Value = (object?)details ?? DBNull.Value;
            var command = sql.Insert("AccountingEvents",
                                     ("EventKey", "{0}"), ("Kind", "{1}"), ("OccurredAt", "{2}"),
                                     ("BlockHeight", "{3}"), ("AmountMsat", "{4}"), ("FeeMsat", "{5}"),
                                     ("Finality", "{6}"), ("Flags", "{7}"), ("LedgerSeq", "{8}"),
                                     ("Hash", "{9}"), ("Details", "{10}"));
            await context.Database.ExecuteSqlRawAsync(command,
                [key, (int)kind, s_at.UtcTicks, 100, 1_000L, 0L, (byte)1, 0, seq, hash,
                 detailsParameter], cancellationToken);
        }
        await InsertAsync("legacy:re:95", AccountingEventKind.WalletReceived);
        await InsertAsync("explicit", AccountingEventKind.WalletReceived);
        await InsertAsync("unrecorded", AccountingEventKind.WalletReceived);
        await InsertAsync("still-standing", AccountingEventKind.WalletReceived);
        await InsertAsync("legacy:re:95:rev:100", AccountingEventKind.Reversal);
        await InsertAsync("different:rev:100", AccountingEventKind.Reversal, "{\"reverses\":\"explicit\"}");
        await InsertAsync("unrecorded:rev:100:unrecorded", AccountingEventKind.Reversal);
        await InsertAsync("invalid-no-marker", AccountingEventKind.Reversal);
        await InsertAsync("still-standing:rev:100", AccountingEventKind.InvoiceSettled);

        await migrator.MigrateAsync(cancellationToken: cancellationToken);

        var rows = await context.AccountingEvents.AsNoTracking().OrderBy(e => e.LedgerSeq).ToListAsync(cancellationToken);
        Assert.All(rows, row => Assert.Equal(hash, row.Hash));
        Assert.Equal(Enumerable.Range(1, rows.Count).Select(i => (long?)i), rows.Select(e => e.LedgerSeq));
        Assert.Equal("legacy:re:95", rows.Single(e => e.EventKey == "legacy:re:95:rev:100").ReversesEventKey);
        Assert.Equal("explicit", rows.Single(e => e.EventKey == "different:rev:100").ReversesEventKey);
        Assert.Equal("unrecorded", rows.Single(e => e.EventKey == "unrecorded:rev:100:unrecorded").ReversesEventKey);
        Assert.Null(rows.Single(e => e.EventKey == "invalid-no-marker").ReversesEventKey);
        Assert.Null(rows.Single(e => e.EventKey == "still-standing:rev:100").ReversesEventKey);
        var history = await new AccountingEventDbRepository(context).GetWalletHistoryAsync(100, 100, 0, 100, cancellationToken);
        Assert.Equal("still-standing", Assert.Single(history).EventKey);
    }
}