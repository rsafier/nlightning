using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Node;

/// <summary>
/// Provider-agnostic proof for migration <c>AddPeerStorage</c> (BOLT 1 <c>option_provide_storage</c>), shared by the
/// SQLite test and the Docker Postgres/SQL Server tests: the schema right before it moves forward, then a maximal
/// (65531-byte) and an empty blob round-trip with their times to the tick, an upsert replaces the blob (one row per
/// peer), and a delete removes it.
/// </summary>
internal static class PeerStorageSchemaRoundTrip
{
    private const string MigrationName = "_AddPeerStorage";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddPeerStorage, then the migration
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

        var peerA = Key(0x11);
        var peerB = Key(0x22);
        var maximal = Enumerable.Range(0, PeerStorageConstants.MaxBlobLength).Select(i => (byte)(i * 31)).ToArray();
        var storedAt = new DateTimeOffset(2026, 9, 26, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_567);
        await using (var context = contextFactory())
        {
            var repository = new PeerStorageDbRepository(context);
            await repository.UpsertAsync(new StoredPeerBlob(peerA, maximal, storedAt));
            await repository.UpsertAsync(new StoredPeerBlob(peerB, [], storedAt.AddMinutes(1)));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every byte and the time to the tick
        await using (var context = contextFactory())
        {
            var repository = new PeerStorageDbRepository(context);
            var reloaded = await repository.GetAsync(peerA);
            Assert.NotNull(reloaded);
            Assert.Equal(maximal, reloaded.Blob);
            Assert.Equal(storedAt, reloaded.UpdatedAt);
            Assert.Empty((await repository.GetAsync(peerB))!.Blob);
            Assert.Equal(2, (await repository.GetAllAsync()).Count);
            Assert.Null(await repository.GetAsync(Key(0x33)));

            // Act: the latest replaces the old one
            await repository.UpsertAsync(new StoredPeerBlob(peerA, [0x01, 0x02], storedAt.AddMinutes(2)));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new PeerStorageDbRepository(context);
            var replaced = await repository.GetAsync(peerA);
            Assert.Equal(new byte[] { 0x01, 0x02 }, replaced!.Blob);
            Assert.Equal(storedAt.AddMinutes(2), replaced.UpdatedAt);
            Assert.Equal(2, (await repository.GetAllAsync()).Count);

            // Act: delete
            await repository.DeleteAsync(peerB);
            await repository.DeleteAsync(Key(0x33));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new PeerStorageDbRepository(context);
            Assert.Equal(peerA, Assert.Single(await repository.GetAllAsync()).PeerNodeId);
        }
    }

    private static CompactPubKey Key(byte fill)
    {
        var key = Enumerable.Repeat(fill, 33).ToArray();
        key[0] = 0x03;
        return new CompactPubKey(key);
    }
}