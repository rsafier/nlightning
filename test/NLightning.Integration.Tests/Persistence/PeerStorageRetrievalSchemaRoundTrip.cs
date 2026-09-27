using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Node;

/// <summary>
/// Provider-agnostic proof for migration <c>AddPeerStorageRetrievals</c> (NL-432), shared by the SQLite test and the
/// Docker Postgres test: the schema right before it moves forward, then a maximal (65531-byte) retrieval naming lost
/// channels and an empty one round-trip with their times to the tick and all three <c>MatchesLastSent</c> values, and
/// an upsert replaces a peer's row (one row per peer, the latest retrieval).
/// </summary>
internal static class PeerStorageRetrievalSchemaRoundTrip
{
    private const string MigrationName = "_AddPeerStorageRetrievals";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddPeerStorageRetrievals, then the migration
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
        var peerC = Key(0x44);
        var maximal = Enumerable.Range(0, PeerStorageConstants.MaxBlobLength).Select(i => (byte)(i * 7)).ToArray();
        var receivedAt = new DateTimeOffset(2026, 9, 27, 8, 9, 10, TimeSpan.Zero).AddTicks(7_654_321);
        ChannelId[] lost = [ChannelIdOf(0xA1), ChannelIdOf(0xA2), ChannelIdOf(0xA3)];
        await using (var context = contextFactory())
        {
            var repository = new PeerStorageRetrievalDbRepository(context);
            await repository.UpsertAsync(new StoredPeerRetrieval(peerA, receivedAt, maximal, null, lost));
            await repository.UpsertAsync(new StoredPeerRetrieval(peerB, receivedAt.AddMinutes(1), [], true, []));
            await repository.UpsertAsync(new StoredPeerRetrieval(peerC, receivedAt.AddMinutes(2), [0x09], false, []));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every byte, the lost channels in order and the time to the tick
        await using (var context = contextFactory())
        {
            var repository = new PeerStorageRetrievalDbRepository(context);
            var reloaded = await repository.GetAsync(peerA);
            Assert.NotNull(reloaded);
            Assert.Equal(maximal, reloaded.Blob);
            Assert.Equal(receivedAt, reloaded.ReceivedAt);
            Assert.Null(reloaded.MatchesLastSent);
            Assert.Equal(lost, reloaded.UnknownChannelIds);

            var empty = await repository.GetAsync(peerB);
            Assert.Empty(empty!.Blob);
            Assert.True(empty.MatchesLastSent);
            Assert.Empty(empty.UnknownChannelIds);
            Assert.False((await repository.GetAsync(peerC))!.MatchesLastSent);
            Assert.Equal(3, (await repository.GetAllAsync()).Count);
            Assert.Null(await repository.GetAsync(Key(0x33)));

            // Act: the latest replaces the old one
            await repository.UpsertAsync(new StoredPeerRetrieval(peerA, receivedAt.AddHours(1), [0x01, 0x02], true,
                                                                 [lost[1]]));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new PeerStorageRetrievalDbRepository(context);
            var replaced = await repository.GetAsync(peerA);
            Assert.Equal(new byte[] { 0x01, 0x02 }, replaced!.Blob);
            Assert.Equal(receivedAt.AddHours(1), replaced.ReceivedAt);
            Assert.True(replaced.MatchesLastSent);
            Assert.Equal(lost[1], Assert.Single(replaced.UnknownChannelIds));
            Assert.Equal(3, (await repository.GetAllAsync()).Count);
        }
    }

    private static CompactPubKey Key(byte fill)
    {
        var key = Enumerable.Repeat(fill, 33).ToArray();
        key[0] = 0x03;
        return new CompactPubKey(key);
    }

    private static ChannelId ChannelIdOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());
}