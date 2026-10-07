using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Bitcoin;

internal static class SilentPaymentSchemaRoundTrip
{
    public static async Task AssertAsync(Func<NLightningDbContext> factory, DatabaseType provider,
        CancellationToken cancellationToken)
    {
        // Arrange: preserve a pre-SP wallet coin through the nullable-owner migration.
        var legacyId = new TxId(Enumerable.Repeat((byte)1, 32).ToArray());
        await using (var context = factory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith("_AddSilentPayments", StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            var sql = new MigrationSqlDialect(provider);
            var blob = provider switch
            {
                DatabaseType.PostgreSql => $"decode('{Convert.ToHexString((byte[])legacyId)}, 'hex')",
                DatabaseType.MicrosoftSql => $"0x{Convert.ToHexString((byte[])legacyId)}",
                _ => $"X'{Convert.ToHexString((byte[])legacyId)}'"
            };
            await context.Database.ExecuteSqlRawAsync(sql.Insert("WalletAddresses", ("Index", "7"),
                ("IsChange", sql.Bool(false)), ("AddressType", "1"), ("Address", "'bcrt1qlegacy'"),
                ("IsReserved", sql.Bool(false))), cancellationToken);
            await context.Database.ExecuteSqlRawAsync(sql.Insert("Utxos", ("TransactionId", blob), ("Index", "0"),
                ("AmountSats", "50000"), ("BlockHeight", "100"), ("AddressIndex", "7"),
                ("IsAddressChange", sql.Bool(false)), ("AddressType", "1")), cancellationToken);
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
        }

        var output = Output(2, 110);
        var ignored = Output(3, 111) with { Ignored = true, AmountSats = 1 };
        var cursorHash = new Hash(Enumerable.Repeat((byte)4, 32).ToArray());
        await using (var context = factory())
        {
            var repo = new SilentPaymentDbRepository(context);
            await repo.UpsertOutputAsync(output, cancellationToken);
            await repo.UpsertOutputAsync(ignored, cancellationToken);
            repo.AddLabel(new SilentPaymentLabelModel(9, "salary", 100));
            await repo.SetScanStateAsync(new SilentPaymentScanState(100, 112, 108, cursorHash, 111, "Core", 111, cursorHash, 50), cancellationToken);
            new UtxoDbRepository(context).Add(new UtxoModel(output));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act: restart, spend the coin, and retain the metadata needed for a disconnected spend.
        var spendId = new TxId(Enumerable.Repeat((byte)8, 32).ToArray());
        await using (var context = factory())
        {
            var repo = new SilentPaymentDbRepository(context);
            var utxos = new UtxoDbRepository(context);
            var legacy = await utxos.GetByIdAsync(legacyId, 0, true);
            Assert.Equal(50_000, legacy!.Amount.Satoshi);
            Assert.NotNull(legacy.WalletAddress);
            var coin = await utxos.GetByIdAsync(output.TransactionId, output.Index);
            Assert.NotNull(coin!.SilentPayment);
            Assert.Null(coin.WalletAddress);
            Assert.Equal(output.OutputKey, coin.SilentPayment.OutputKey);
            Assert.Equal(output.Tweak, coin.SilentPayment.Tweak);
            Assert.Equal(9u, Assert.Single(await repo.GetLabelsAsync(cancellationToken)).M);
            var state = await repo.GetScanStateAsync(cancellationToken);
            Assert.Equal(cursorHash, state!.RescanCursorHash);
            Assert.Equal(100u, state.BirthdayHeight);
            Assert.Equal(112u, state.LiveFromHeight);
            Assert.Equal(111u, state.RescanTargetHeight);
            Assert.Equal("Core", state.PrevoutSource);
            Assert.Equal(111u, state.LiveCursorHeight);
            Assert.Equal(cursorHash, state.LiveCursorHash);
            Assert.Equal(50u, state.RecoveryLabelCount);
            utxos.Spend(coin);
            await repo.SetSpentAsync(output.TransactionId, output.Index, spendId, 112, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = factory())
        {
            var repo = new SilentPaymentDbRepository(context);
            Assert.Null(await new UtxoDbRepository(context).GetByIdAsync(output.TransactionId, output.Index));
            var retained = await repo.GetOutputAsync(output.TransactionId, output.Index, cancellationToken);
            Assert.Equal(spendId, retained!.SpentByTransactionId);
            Assert.Equal(112u, retained.SpentAtHeight);
            Assert.True((await repo.GetOutputAsync(ignored.TransactionId, ignored.Index, cancellationToken))!.Ignored);
            await repo.UpsertOutputAsync(output, cancellationToken);
            new UtxoDbRepository(context).Add(new UtxoModel(output));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act: rewind the creating blocks; deletion of both rows is one save, leaving the legacy coin intact.
        await using (var context = factory())
        {
            var repo = new SilentPaymentDbRepository(context);
            var utxos = new UtxoDbRepository(context);
            utxos.Spend((await utxos.GetByIdAsync(output.TransactionId, output.Index))!);
            await repo.DeleteOutputsAboveHeightAsync(109, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
        await using (var context = factory())
        {
            Assert.Empty(await new SilentPaymentDbRepository(context).GetOutputsAsync(cancellationToken));
            Assert.Equal(legacyId, Assert.Single(await new UtxoDbRepository(context).GetUnspentAsync(true)).TxId);
            Assert.False(context.Database.HasPendingModelChanges());
        }
    }

    internal static SilentPaymentOutputModel Output(byte seed, uint height) => new(
        new TxId(Enumerable.Repeat(seed, 32).ToArray()), 1,
        Enumerable.Repeat((byte)5, 32).ToArray(), Enumerable.Repeat((byte)6, 32).ToArray(),
        9, 75_000, height, new Hash(Enumerable.Repeat((byte)7, 32).ToArray()));
}