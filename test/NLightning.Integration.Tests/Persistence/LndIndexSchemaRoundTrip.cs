using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;

/// <summary>
/// Provider-agnostic proof for migration <c>AddLndIndexes</c> (NL-1165), shared by the SQLite test and the Postgres
/// test: the invoices and payments that exist get LND's dense indexes in creation order (<c>settle_index</c> in settle
/// order, only for settled invoices), trampoline relay legs get none.
/// </summary>
internal static class LndIndexSchemaRoundTrip
{
    private const string MigrationName = "_AddLndIndexes";

    private static readonly DateTimeOffset s_createdAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly CompactPubKey s_payee =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddLndIndexes, three invoices (the second settled last, the third first) and
        // three payments (the middle one a trampoline relay leg), created out of hash order
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            await SeedAsync(context, databaseType, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert
        await using (var context = contextFactory())
        {
            var invoices = await context.Invoices.AsNoTracking().OrderBy(i => i.AddIndex).ToListAsync(cancellationToken);
            Assert.Equal(new long?[] { 1, 2, 3 }, invoices.Select(i => i.AddIndex));
            Assert.Equal(new byte[] { 0x53, 0x52, 0x51 }, invoices.Select(i => ((byte[])i.PaymentHash)[0]));
            Assert.Equal(new long?[] { null, 2, 1 }, invoices.Select(i => i.SettleIndex));

            var payments = await context.Payments.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(1, payments.Single(p => ((byte[])p.PaymentHash)[0] == 0x63).PaymentIndex);
            Assert.Null(payments.Single(p => ((byte[])p.PaymentHash)[0] == 0x62).PaymentIndex);
            Assert.Equal(2, payments.Single(p => ((byte[])p.PaymentHash)[0] == 0x61).PaymentIndex);
        }
    }

    private static async Task SeedAsync(NLightningDbContext context, DatabaseType databaseType,
                                        CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        var settledFirst = s_createdAt.AddHours(1).UtcTicks;
        var settledLast = s_createdAt.AddHours(2).UtcTicks;
        (byte Tag, int Minutes, int Status, long? SettledAt)[] invoices =
        [
            (0x51, 2, 2, settledFirst), (0x52, 1, 2, settledLast), (0x53, 0, 0, null)
        ];
        foreach (var (tag, minutes, status, settledAt) in invoices)
        {
            var columns = new List<(string, string)>
            {
                ("PaymentHash", "{0}"), ("Preimage", "{1}"), ("PaymentSecret", "{2}"), ("Bolt11", "{3}"), ("Kind", "0"),
                ("CreatedAt", $"{s_createdAt.AddMinutes(minutes).UtcTicks}"), ("ExpirySeconds", "3600"),
                ("MinFinalCltvExpiry", "18"), ("Status", $"{status}")
            };
            if (settledAt is { } at)
                columns.Add(("SettledAt", $"{at}"));
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("Invoices", [.. columns]),
                [Enumerable.Repeat(tag, 32).ToArray(), Enumerable.Repeat((byte)0xA1, 32).ToArray(),
                 Enumerable.Repeat((byte)0xA2, 32).ToArray(), $"lnbcrt{tag}"], cancellationToken);
        }

        (byte Tag, int Minutes, bool Relay)[] payments = [(0x61, 2, false), (0x62, 1, true), (0x63, 0, false)];
        foreach (var (tag, minutes, relay) in payments)
        {
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("Payments",
                           ("PaymentHash", "{0}"), ("PayeeNodeId", "{1}"), ("AmountMsat", "21000"), ("FeeMsat", "0"),
                           ("CreatedAt", $"{s_createdAt.AddMinutes(minutes).UtcTicks}"), ("Status", "1"),
                           ("Bolt11", "{2}"), ("IsTrampolineRelay", sql.Bool(relay))),
                [Enumerable.Repeat(tag, 32).ToArray(), (byte[])s_payee, $"lnbcrt{tag}"], cancellationToken);
        }
    }
}