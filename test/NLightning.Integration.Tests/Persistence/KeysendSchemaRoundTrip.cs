using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddPaymentCustomRecords</c> (NL-460), shared by the SQLite test and the
/// Docker Postgres/SQL Server tests: the keysend rows whose custom records the <c>Bolt12InvoiceBytes</c> column held
/// (lane lh1-l3) move into <c>CustomRecords</c> and the borrowed bytes are cleared, while BOLT 11 and BOLT 12 rows
/// keep theirs, and keysend rows with and without records round-trip through the repositories afterwards.
/// </summary>
internal static class KeysendSchemaRoundTrip
{
    private const string MigrationName = "_AddPaymentCustomRecords";

    private static readonly DateTimeOffset s_createdAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly CompactPubKey s_payee =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        var records = new CustomRecord[] { new(7629169, "boost"u8), new(65536, [0x01, 0x02]) };
        var recordBytes = CustomRecordCodec.Encode(records);
        var invoiceBytes = new byte[] { 0xFD, 0x01, 0x02, 0x03 };
        var keysendInvoiceHash = new Hash(Enumerable.Repeat((byte)0x21, 32).ToArray());
        var bolt11InvoiceHash = new Hash(Enumerable.Repeat((byte)0x22, 32).ToArray());
        var keysendPaymentHash = new Hash(Enumerable.Repeat((byte)0x23, 32).ToArray());
        var bolt12PaymentHash = new Hash(Enumerable.Repeat((byte)0x24, 32).ToArray());

        // Arrange: the schema right before AddPaymentCustomRecords, with keysend rows in the borrowed column
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            await SeedAsync(context, databaseType, keysendInvoiceHash, bolt11InvoiceHash, keysendPaymentHash,
                           bolt12PaymentHash, recordBytes, invoiceBytes, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the keysend rows' records moved, the borrowed bytes are cleared, and every other row is untouched
        await using (var context = contextFactory())
        {
            var keysendInvoice = await context.Invoices.AsNoTracking()
                                            .SingleAsync(i => i.PaymentHash == keysendInvoiceHash, cancellationToken);
            Assert.Equal((byte)InvoiceKind.Keysend, keysendInvoice.Kind);
            Assert.Equal(recordBytes, keysendInvoice.CustomRecords);
            Assert.Null(keysendInvoice.Bolt12InvoiceBytes);

            var bolt11Invoice = await context.Invoices.AsNoTracking()
                                          .SingleAsync(i => i.PaymentHash == bolt11InvoiceHash, cancellationToken);
            Assert.Equal((byte)InvoiceKind.Bolt11, bolt11Invoice.Kind);
            Assert.Null(bolt11Invoice.CustomRecords);
            Assert.Null(bolt11Invoice.Bolt12InvoiceBytes);

            var keysendPayment = await context.Payments.AsNoTracking()
                                          .SingleAsync(p => p.PaymentHash == keysendPaymentHash, cancellationToken);
            Assert.Null(keysendPayment.Bolt11);
            Assert.Null(keysendPayment.OfferBolt12);
            Assert.Equal(recordBytes, keysendPayment.CustomRecords);
            Assert.Null(keysendPayment.Bolt12InvoiceBytes);

            var bolt12Payment = await context.Payments.AsNoTracking()
                                          .SingleAsync(p => p.PaymentHash == bolt12PaymentHash, cancellationToken);
            Assert.Null(bolt12Payment.Bolt11);
            Assert.Equal("lno1bolt12payment", bolt12Payment.OfferBolt12);
            Assert.Equal(invoiceBytes, bolt12Payment.Bolt12InvoiceBytes);
            Assert.Null(bolt12Payment.CustomRecords);
        }

        // Assert: the keysend rows read back through the repositories, with and without records
        var payment = new PaymentModel(new Hash(Enumerable.Repeat((byte)0x25, 32).ToArray()), null, s_payee,
                                       LightningMoney.Satoshis(21), LightningMoney.Zero, s_createdAt,
                                       keysend: new KeysendDetails(records));
        await SaveAsync(contextFactory, c => new PaymentDbRepository(c).AddAsync(payment), cancellationToken);
        var invoice = new InvoiceModel(new Hash(Enumerable.Repeat((byte)0x26, 32).ToArray()),
                                       new Secret(Enumerable.Repeat((byte)0x27, 32).ToArray()), new Secret(new byte[32]),
                                       null, null, null, s_createdAt, 86_400, 18, keysend: new KeysendDetails([]));
        await SaveAsync(contextFactory, c => new InvoiceDbRepository(c).AddAsync(invoice), cancellationToken);

        await using var readContext = contextFactory();
        var storedPayment = await new PaymentDbRepository(readContext).GetByPaymentHashAsync(payment.PaymentHash);
        Assert.Equal(records.OrderBy(r => r.Type), storedPayment!.Keysend!.CustomRecords);
        var storedInvoice = await new InvoiceDbRepository(readContext).GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.Equal(InvoiceKind.Keysend, storedInvoice!.Kind);
        Assert.Empty(storedInvoice.Keysend!.CustomRecords);
    }

    private static async Task SeedAsync(NLightningDbContext context, DatabaseType databaseType,
                                        Hash keysendInvoiceHash, Hash bolt11InvoiceHash, Hash keysendPaymentHash,
                                        Hash bolt12PaymentHash, byte[] recordBytes, byte[] invoiceBytes,
                                        CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);

        // A keysend record (Kind 2) whose records sit in Bolt12InvoiceBytes, and a BOLT 11 invoice (Kind 0)
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Invoices",
                       ("PaymentHash", "{0}"), ("Preimage", "{1}"), ("PaymentSecret", "{2}"), ("Kind", "2"),
                       ("CreatedAt", $"{s_createdAt.UtcTicks}"), ("ExpirySeconds", "3600"),
                       ("MinFinalCltvExpiry", "18"), ("Status", "0"), ("Bolt12InvoiceBytes", "{3}")),
            [(byte[])keysendInvoiceHash, Enumerable.Repeat((byte)0xA1, 32).ToArray(),
             Enumerable.Repeat((byte)0xA2, 32).ToArray(), recordBytes], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Invoices",
                       ("PaymentHash", "{0}"), ("Preimage", "{1}"), ("PaymentSecret", "{2}"), ("Bolt11", "{3}"),
                       ("Kind", "0"), ("CreatedAt", $"{s_createdAt.UtcTicks}"), ("ExpirySeconds", "3600"),
                       ("MinFinalCltvExpiry", "18"), ("Status", "0")),
            [(byte[])bolt11InvoiceHash, Enumerable.Repeat((byte)0xA3, 32).ToArray(),
             Enumerable.Repeat((byte)0xA4, 32).ToArray(), "lnbcrt1invoice"], cancellationToken);

        // A keysend payment (no invoice string, no offer, records in Bolt12InvoiceBytes) and a BOLT 12 payment whose
        // bytes are the invoice's
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Payments",
                       ("PaymentHash", "{0}"), ("PayeeNodeId", "{1}"), ("AmountMsat", "21000"), ("FeeMsat", "0"),
                       ("CreatedAt", $"{s_createdAt.UtcTicks}"), ("Status", "0"), ("Bolt12InvoiceBytes", "{2}")),
            [(byte[])keysendPaymentHash, (byte[])s_payee, recordBytes], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Payments",
                       ("PaymentHash", "{0}"), ("PayeeNodeId", "{1}"), ("AmountMsat", "21000"), ("FeeMsat", "0"),
                       ("CreatedAt", $"{s_createdAt.UtcTicks}"), ("Status", "0"), ("OfferBolt12", "{2}"),
                       ("Bolt12InvoiceBytes", "{3}")),
            [(byte[])bolt12PaymentHash, (byte[])s_payee, "lno1bolt12payment", invoiceBytes], cancellationToken);
    }

    private static async Task SaveAsync(Func<NLightningDbContext> contextFactory,
                                        Func<NLightningDbContext, Task> stage, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        await stage(context);
        await context.SaveChangesAsync(cancellationToken);
    }
}