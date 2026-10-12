using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Protocol.Onion;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Payment;
using Infrastructure.Repositories.Memory;

/// <summary>
/// Provider-agnostic proof for migration <c>AddOnionReplaySet</c> (NL-078), shared by the SQLite test and the Docker
/// Postgres/SQL Server tests: rows written before the migration are untouched and the new table starts empty, entries
/// round-trip with the extreme ids and heights, expiry pruning deletes only what the chain passed, and the persistent
/// replay store detects a replay across a restart on this provider.
/// </summary>
internal static class OnionReplaySchemaRoundTrip
{
    private const string MigrationName = "_AddOnionReplaySet";

    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0x0A, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0x0B, 32).ToArray());

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddOnionReplaySet, with a processed block in it
        var blockHash = Enumerable.Repeat((byte)0x33, 32).ToArray();
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            var sql = new MigrationSqlDialect(databaseType);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("BlockHeaders", ("Height", "812345"), ("BlockHash", "{0}"), ("PreviousBlockHash", "{1}")),
                [blockHash, new byte[32]], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the seeded row moved forward untouched, the replay set is empty
        await using (var context = contextFactory())
        {
            var header = await context.BlockHeaders.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal(812_345U, header.Height);
            Assert.Equal(blockHash, (byte[])header.BlockHash);
            Assert.Empty(await context.OnionReplayEntries.ToListAsync(cancellationToken));
        }

        await AssertTableRoundTripsAsync(contextFactory, cancellationToken);
        await AssertReplayDetectedAcrossRestartAsync(contextFactory, cancellationToken);
    }

    /// <summary>
    /// Entries round-trip (including <c>ulong.MaxValue</c> HTLC ids and <c>uint.MaxValue</c> heights), a duplicate HMAC
    /// is refused by the primary key, and pruning deletes only entries below the height.
    /// </summary>
    public static async Task AssertTableRoundTripsAsync(Func<NLightningDbContext> contextFactory,
                                                        CancellationToken cancellationToken)
    {
        var early = new OnionReplayEntry(Hmac(0x01), s_channelA, 0, 100);
        var extreme = new OnionReplayEntry(Hmac(0x02), s_channelB, ulong.MaxValue, uint.MaxValue);
        var boundary = new OnionReplayEntry(Hmac(0x03), s_channelA, 7, 200);
        await using (var context = contextFactory())
        {
            var repository = new OnionReplayDbRepository(context);
            repository.Add(early);
            repository.Add(extreme);
            repository.Add(boundary);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new OnionReplayDbRepository(context);
            AssertEntry(early, await repository.GetByHmacAsync(Hmac(0x01)));
            AssertEntry(extreme, await repository.GetByHmacAsync(Hmac(0x02)));
            AssertEntry(boundary, await repository.GetByHmacAsync(Hmac(0x03)));
            Assert.Null(await repository.GetByHmacAsync(Hmac(0x04)));
            Assert.Equal(3, await repository.CountAsync());
        }

        // The HMAC is the primary key
        await using (var context = contextFactory())
        {
            new OnionReplayDbRepository(context).Add(new OnionReplayEntry(Hmac(0x01), s_channelB, 1, 999));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }

        // Pruning at height 200: 100 is gone, 200 (not passed yet) and uint.MaxValue stay
        await using (var context = contextFactory())
            Assert.Equal(1, await new OnionReplayDbRepository(context).DeleteExpiredAsync(200));

        await using (var context = contextFactory())
        {
            var repository = new OnionReplayDbRepository(context);
            Assert.Null(await repository.GetByHmacAsync(Hmac(0x01)));
            AssertEntry(boundary, await repository.GetByHmacAsync(Hmac(0x03)));
            Assert.Equal(1, await repository.DeleteExpiredAsync(201));
            Assert.Equal(0, await repository.DeleteExpiredAsync(201));
            AssertEntry(extreme, await repository.GetByHmacAsync(Hmac(0x02)));
            Assert.Equal(1, await repository.CountAsync());
        }
    }

    /// <summary>
    /// Two <see cref="PersistentOnionReplayStore"/> instances in sequence over the same database (a node restart): the
    /// second one refuses an HMAC the first recorded for another HTLC, and accepts it for the recording HTLC.
    /// </summary>
    public static async Task AssertReplayDetectedAcrossRestartAsync(Func<NLightningDbContext> contextFactory,
                                                                    CancellationToken cancellationToken)
    {
        var hmac = Hmac(0x40);
        await using (var before = CreateStoreServices(contextFactory))
        {
            var store = before.GetRequiredService<IOnionReplayStore>();
            Assert.True(await store.TryAddAsync(hmac, s_channelA, 5, 900, cancellationToken));
            Assert.False(await store.TryAddAsync(hmac, s_channelB, 5, 900, cancellationToken));
        }

        await using var after = CreateStoreServices(contextFactory);
        var restarted = after.GetRequiredService<IOnionReplayStore>();
        Assert.False(await restarted.TryAddAsync(hmac, s_channelB, 5, 900, cancellationToken));
        Assert.False(await restarted.TryAddAsync(hmac, s_channelA, 6, 900, cancellationToken));
        Assert.True(await restarted.TryAddAsync(hmac, s_channelA, 5, 900, cancellationToken));

        // Once the chain passed the HTLC's expiry the entry goes, and the HMAC is unknown again
        Assert.Equal(1, await restarted.PruneAsync(901, cancellationToken));
        Assert.True(await restarted.TryAddAsync(hmac, s_channelB, 5, 1_000, cancellationToken));
    }

    /// <summary>The persistent store over a scoped production <see cref="UnitOfWork"/> on
    /// <paramref name="contextFactory"/>.</summary>
    internal static ServiceProvider CreateStoreServices(Func<NLightningDbContext> contextFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(
                                            contextFactory(),
                                            Microsoft.Extensions.Logging.Abstractions.NullLogger<UnitOfWork>.Instance,
                                            new Sha256(), sp.GetRequiredService<IUtxoMemoryRepository>()));
        services.AddPersistentOnionReplayStore();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    internal static byte[] Hmac(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static void AssertEntry(OnionReplayEntry expected, OnionReplayEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Hmac.ToArray(), actual.Hmac.ToArray());
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.HtlcId, actual.HtlcId);
        Assert.Equal(expected.ExpiryHeight, actual.ExpiryHeight);
    }
}