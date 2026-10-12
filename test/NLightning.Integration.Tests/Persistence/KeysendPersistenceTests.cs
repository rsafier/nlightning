using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Keysend rows on SQLite (lane lh1-l3): the custom records live in the <c>CustomRecords</c> column
/// (migration <c>AddPaymentCustomRecords</c>, NL-460), a row whose bytes are not a custom record stream never breaks a
/// payment hash lookup (the switch reads invoices under its hash lock, the payment service reads payments to
/// reconcile), and the rows written before the migration move out of the borrowed <c>Bolt12InvoiceBytes</c> column.
/// </summary>
public class KeysendPersistenceTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly CompactPubKey s_payee =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    [Fact]
    public async Task Given_KeysendRowsFromBeforeAddPaymentCustomRecords_When_Migrated_Then_TheRecordsMoveIntoTheirColumn()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (the same rows and assertions as the Docker Postgres/SQL Server tests)
        await KeysendSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_KeysendPaymentAndRecord_When_SavedAndReloaded_Then_TheCustomRecordsRoundTrip()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        CustomRecord[] records = [new(7629169, "boost"u8), new(65536, [0x01, 0x02])];
        var payment = CreatePayment(0x21, records);
        var invoice = CreateRecord(0x22, records);

        // Act
        await SaveAsync(db, async c =>
        {
            await new PaymentDbRepository(c).AddAsync(payment);
            await new InvoiceDbRepository(c).AddAsync(invoice);
        });

        // Assert
        await using var context = db.CreateDbContext();
        var storedPayment = await new PaymentDbRepository(context).GetByPaymentHashAsync(payment.PaymentHash);
        Assert.Equal(records.OrderBy(r => r.Type), storedPayment!.Keysend!.CustomRecords);
        var storedInvoice = await new InvoiceDbRepository(context).GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.Equal(InvoiceKind.Keysend, storedInvoice!.Kind);
        Assert.Equal(records.OrderBy(r => r.Type), storedInvoice.Keysend!.CustomRecords);
    }

    [Fact]
    public async Task Given_RowsWithUnreadableCustomRecordBytes_When_ReadByHash_Then_KeysendWithoutRecordsAndNoThrow()
    {
        // Arrange: a keysend payment and a keysend record whose column holds bytes the codec refuses
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var payment = CreatePayment(0x31, []);
        var invoice = CreateRecord(0x32, []);
        await SaveAsync(db, async c =>
        {
            await new PaymentDbRepository(c).AddAsync(payment);
            await new InvoiceDbRepository(c).AddAsync(invoice);
        });
        byte[] garbage = [0xfe, 0x00, 0x01];
        await using (var context = db.CreateDbContext())
        {
            (await context.Payments.SingleAsync(TestContext.Current.CancellationToken)).CustomRecords = garbage;
            (await context.Invoices.SingleAsync(TestContext.Current.CancellationToken)).CustomRecords = garbage;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using var readContext = db.CreateDbContext();
        var storedPayment = await new PaymentDbRepository(readContext).GetByPaymentHashAsync(payment.PaymentHash);
        var storedInvoice = await new InvoiceDbRepository(readContext).GetByPaymentHashAsync(invoice.PaymentHash);

        // Assert
        Assert.Empty(storedPayment!.Keysend!.CustomRecords);
        Assert.Empty(storedInvoice!.Keysend!.CustomRecords);
        Assert.Equal(InvoiceKind.Keysend, storedInvoice.Kind);
    }

    private static PaymentModel CreatePayment(byte seed, IReadOnlyList<CustomRecord> records) =>
        new(new Hash(Enumerable.Repeat(seed, 32).ToArray()), null, s_payee, LightningMoney.Satoshis(21),
            LightningMoney.Zero, s_now, keysend: new KeysendDetails(records));

    private static InvoiceModel CreateRecord(byte seed, IReadOnlyList<CustomRecord> records) =>
        new(new Hash(Enumerable.Repeat(seed, 32).ToArray()), new Secret(Enumerable.Repeat((byte)(seed + 1), 32).ToArray()),
            new Secret(new byte[32]), null, null, null, s_now, 86_400, 18, keysend: new KeysendDetails(records));

    private static async Task SaveAsync(SqliteDbTestContext db, Func<NLightningDbContext, Task> stage)
    {
        await using var context = db.CreateDbContext();
        await stage(context);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}