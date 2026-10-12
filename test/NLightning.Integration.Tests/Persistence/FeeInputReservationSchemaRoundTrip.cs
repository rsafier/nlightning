using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Bitcoin;

/// <summary>
/// Provider-agnostic proof for migration <c>AddFeeInputReservations</c> (BOLT 5 plan O7-T1), shared by the SQLite test
/// and the Docker Postgres/SQL Server tests: the schema right before it moves forward, then reservations round-trip
/// (every field, largest amounts and longest purpose and scripts, oldest first), the outpoint key refuses a second
/// reservation of an output, and a delete removes the reservation with its inputs.
/// </summary>
internal static class FeeInputReservationSchemaRoundTrip
{
    private const string MigrationName = "_AddFeeInputReservations";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddFeeInputReservations, then the migration
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        var withChange = new FeeInputReservation(
            Guid.NewGuid(), new string('p', 128),
            [
                Input(0x51, uint.MaxValue, 2_100_000_000_000_000, AddressType.P2Tr, 34),
                Input(0x52, 0, 70_000, AddressType.P2Wpkh, 22)
            ], LightningMoney.Satoshis(3_456), LightningMoney.Satoshis(1_234),
            new BitcoinScript(Enumerable.Repeat((byte)0x61, 64).ToArray()));
        var withoutChange = new FeeInputReservation(Guid.NewGuid(), "cpfp:no-change",
                                                    [Input(0x53, 7, 4_000, AddressType.P2Wpkh, 22)],
                                                    LightningMoney.Satoshis(4_000), LightningMoney.Zero, null);
        await using (var context = contextFactory())
        {
            var repository = new FeeInputReservationDbRepository(context);
            repository.Add(withoutChange, new DateTimeOffset(2026, 9, 26, 12, 0, 1, TimeSpan.Zero));
            repository.Add(withChange, new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every field, in input order, oldest reservation first
        await using (var context = contextFactory())
        {
            var repository = new FeeInputReservationDbRepository(context);
            var reloaded = await repository.GetByIdAsync(withChange.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(withChange.Purpose, reloaded.Purpose);
            Assert.Equal(withChange.Inputs, reloaded.Inputs);
            Assert.Equal(withChange.Fee.Satoshi, reloaded.Fee.Satoshi);
            Assert.Equal(withChange.ChangeAmount.Satoshi, reloaded.ChangeAmount.Satoshi);
            Assert.Equal(withChange.ChangeScript, reloaded.ChangeScript);

            var noChange = await repository.GetByIdAsync(withoutChange.Id);
            Assert.NotNull(noChange);
            Assert.Null(noChange.ChangeScript);
            Assert.Equal(withoutChange.Inputs, noChange.Inputs);

            Assert.Equal([withChange.Id, withoutChange.Id], (await repository.GetAllAsync()).Select(r => r.Id));
            var outpoints = await repository.GetReservedOutpointsAsync();
            Assert.Equal(3, outpoints.Count);
            Assert.Contains((withChange.Inputs[0].TxId, uint.MaxValue, withChange.Id), outpoints);
        }

        // An outpoint already reserved cannot be reserved again (the key is the last guard)
        await using (var context = contextFactory())
        {
            new FeeInputReservationDbRepository(context).Add(
                new FeeInputReservation(Guid.NewGuid(), "cpfp:twice", [withoutChange.Inputs[0]],
                                        LightningMoney.Satoshis(4_000), LightningMoney.Zero, null),
                DateTimeOffset.UtcNow);
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }

        // A delete takes its inputs with it and leaves the other reservation alone
        await using (var context = contextFactory())
        {
            Assert.True(await new FeeInputReservationDbRepository(context).DeleteAsync(withChange.Id));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new FeeInputReservationDbRepository(context);
            Assert.Null(await repository.GetByIdAsync(withChange.Id));
            Assert.False(await repository.DeleteAsync(withChange.Id));
            var (txId, index, reservationId) = Assert.Single(await repository.GetReservedOutpointsAsync());
            Assert.Equal(withoutChange.Inputs[0].TxId, txId);
            Assert.Equal(7U, index);
            Assert.Equal(withoutChange.Id, reservationId);
        }
    }

    private static WalletInput Input(byte seed, uint index, long amountSat, AddressType type, int scriptLength) =>
        new(ChainWatchSchemaRoundTrip.TxIdOf(seed), index, LightningMoney.Satoshis(amountSat), type,
            Enumerable.Repeat(seed, scriptLength).ToArray(), WalletWeights.GetInputWeight(type));
}