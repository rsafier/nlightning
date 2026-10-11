using Microsoft.EntityFrameworkCore;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Infrastructure.Protocol.Services;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// The peer's shachain survives a restart (NL-136, NL-066, BOLT2 plan N3-T4): BOLT 3 Appendix D insert sequences are
/// exported, written through <see cref="RemoteShachainDbRepository"/> into a real Sqlite schema, read back in a fresh
/// context and loaded into a new <see cref="SecretStorageService"/>, which then behaves exactly like the original.
/// </summary>
public class RemoteShachainPersistenceTests
{
    private static readonly byte[][] s_secrets =
    [
        Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageExpectedSecret1,
        Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageExpectedSecret3,
        Bolt3AppendixDVectors.StorageExpectedSecret4, Bolt3AppendixDVectors.StorageExpectedSecret5,
        Bolt3AppendixDVectors.StorageExpectedSecret6, Bolt3AppendixDVectors.StorageExpectedSecret7,
        Bolt3AppendixDVectors.StorageExpectedSecret8, Bolt3AppendixDVectors.StorageExpectedSecret9,
        Bolt3AppendixDVectors.StorageExpectedSecret10, Bolt3AppendixDVectors.StorageExpectedSecret11,
        Bolt3AppendixDVectors.StorageExpectedSecret12, Bolt3AppendixDVectors.StorageExpectedSecret13,
        Bolt3AppendixDVectors.StorageExpectedSecret14, Bolt3AppendixDVectors.StorageExpectedSecret15
    ];

    /// <summary>
    /// Appendix D "insert_secret" cases: the secrets inserted (by vector number, at indices 2^48-1, 2^48-2, ...) up to
    /// the restart, and the one inserted after it with its expected result.
    /// </summary>
    public static TheoryData<string, int[], int, bool> AppendixDSequences => new()
    {
        { "correct sequence", [0, 1, 2, 3, 4, 5, 6], 7, true },
        { "#1 incorrect", [8], 1, false },
        { "#2 incorrect (#1 derived from incorrect)", [8, 9, 2], 3, false },
        { "#3 incorrect", [0, 1, 10], 3, false },
        { "#4 incorrect (1,2,3 derived from incorrect)", [8, 9, 10, 11, 4, 5, 6], 7, false },
        { "#5 incorrect", [0, 1, 2, 3, 12], 5, false },
        { "#6 incorrect (5 derived from incorrect)", [0, 1, 2, 3, 12, 13, 6], 7, false },
        { "#7 incorrect", [0, 1, 2, 3, 4, 5, 14], 7, false },
        { "#8 incorrect", [0, 1, 2, 3, 4, 5, 6], 15, false }
    };

    [Theory]
    [MemberData(nameof(AppendixDSequences))]
    public async Task Given_AppendixDSequence_When_PersistedAndReloaded_Then_NextInsertBehavesAsWithoutRestart(
        string name, int[] before, int after, bool expected)
    {
        // Arrange
        Assert.NotEmpty(name);
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = await SeedChannelAsync(db);
        using (var original = new SecretStorageService())
        {
            for (var i = 0; i < before.Length; i++)
                Assert.True(original.InsertSecret(s_secrets[before[i]], Bolt3AppendixDVectors.StorageIndexMax - (ulong)i));

            await using var writeContext = db.CreateDbContext();
            await new RemoteShachainDbRepository(writeContext).SaveAsync(channelId, original.Export());
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using var readContext = db.CreateDbContext();
        var entries = await new RemoteShachainDbRepository(readContext).GetByChannelIdAsync(channelId);
        using var reloaded = new SecretStorageService();
        reloaded.Load(entries);
        var result = reloaded.InsertSecret(s_secrets[after], Bolt3AppendixDVectors.StorageIndexMax - (ulong)before.Length);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Given_CorrectSequence_When_PersistedAndReloaded_Then_EveryOldSecretIsDerivable()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = await SeedChannelAsync(db);
        using var original = new SecretStorageService();
        for (var i = 0; i < 8; i++)
            Assert.True(original.InsertSecret(s_secrets[i], Bolt3AppendixDVectors.StorageIndexMax - (ulong)i));

        await using (var writeContext = db.CreateDbContext())
        {
            await new RemoteShachainDbRepository(writeContext).SaveAsync(channelId, original.Export());
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using var readContext = db.CreateDbContext();
        var entries = await new RemoteShachainDbRepository(readContext).GetByChannelIdAsync(channelId);
        using var reloaded = new SecretStorageService();
        reloaded.Load(entries);

        // Assert - index 2^48-8 has 3 trailing zeros, so 4 buckets (0..3) hold everything
        Assert.Equal([0, 1, 2, 3], entries.Select(e => e.Bucket));
        for (var i = 0; i < 8; i++)
            Assert.Equal(new Domain.Crypto.ValueObjects.Secret(s_secrets[i]),
                         reloaded.DeriveOldSecret(Bolt3AppendixDVectors.StorageIndexMax - (ulong)i));
    }

    [Fact]
    public async Task Given_StoredShachain_When_SavedAgainAfterMoreSecrets_Then_BucketsAreUpsertedNotDuplicated()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = await SeedChannelAsync(db);
        using var storage = new SecretStorageService();
        for (var i = 0; i < 3; i++)
            Assert.True(storage.InsertSecret(s_secrets[i], Bolt3AppendixDVectors.StorageIndexMax - (ulong)i));

        await using (var firstContext = db.CreateDbContext())
        {
            await new RemoteShachainDbRepository(firstContext).SaveAsync(channelId, storage.Export());
            await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(storage.InsertSecret(s_secrets[3], Bolt3AppendixDVectors.StorageIndexMax - 3));

        // Act
        await using (var secondContext = db.CreateDbContext())
        {
            await new RemoteShachainDbRepository(secondContext).SaveAsync(channelId, storage.Export());
            await secondContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = db.CreateDbContext();
        var rows = await readContext.RemoteShachains.AsNoTracking().Where(e => e.ChannelId == channelId)
                                    .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(storage.Export().Select(e => (e.Bucket, (long)e.Index)),
                     rows.OrderBy(r => r.Bucket).Select(r => ((int)r.Bucket, r.Index)));
    }

    [Fact]
    public async Task Given_OneUnitOfWork_When_SavedTwiceBeforeSaveChanges_Then_LastExportIsStored()
    {
        // Arrange - regression: the second SaveAsync did not see the rows the first one added (only a database query
        // was used), so it added the same (ChannelId, Bucket) again and EF threw
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = await SeedChannelAsync(db);
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(s_secrets[0], Bolt3AppendixDVectors.StorageIndexMax));
        Assert.True(storage.InsertSecret(s_secrets[1], Bolt3AppendixDVectors.StorageIndexMax - 1));
        var firstExport = storage.Export();
        Assert.True(storage.InsertSecret(s_secrets[2], Bolt3AppendixDVectors.StorageIndexMax - 2));
        Assert.True(storage.InsertSecret(s_secrets[3], Bolt3AppendixDVectors.StorageIndexMax - 3));
        var secondExport = storage.Export();

        // Act
        await using (var context = db.CreateDbContext())
        {
            var repository = new RemoteShachainDbRepository(context);
            await repository.SaveAsync(channelId, firstExport);
            await repository.SaveAsync(channelId, secondExport);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = db.CreateDbContext();
        var entries = await new RemoteShachainDbRepository(readContext).GetByChannelIdAsync(channelId);
        Assert.Equal(secondExport.Select(e => (e.Bucket, e.Index)), entries.Select(e => (e.Bucket, e.Index)));
    }

    [Fact]
    public async Task Given_OneUnitOfWork_When_BucketRemovedThenSavedAgain_Then_RowIsKept()
    {
        // Arrange - a stored bucket dropped by one SaveAsync and needed again by the next one in the same unit of work
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = await SeedChannelAsync(db);
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(s_secrets[0], Bolt3AppendixDVectors.StorageIndexMax));
        Assert.True(storage.InsertSecret(s_secrets[1], Bolt3AppendixDVectors.StorageIndexMax - 1));
        var export = storage.Export();
        await using (var writeContext = db.CreateDbContext())
        {
            await new RemoteShachainDbRepository(writeContext).SaveAsync(channelId, export);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using (var context = db.CreateDbContext())
        {
            var repository = new RemoteShachainDbRepository(context);
            await repository.SaveAsync(channelId, [export[0]]);
            await repository.SaveAsync(channelId, export);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = db.CreateDbContext();
        var entries = await new RemoteShachainDbRepository(readContext).GetByChannelIdAsync(channelId);
        Assert.Equal(export.Select(e => (e.Bucket, e.Index)), entries.Select(e => (e.Bucket, e.Index)));
    }

    [Fact]
    public async Task Given_ChannelWithShachain_When_ChannelDeleted_Then_ShachainRowsAreDeleted()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = await SeedChannelAsync(db);
        using var storage = new SecretStorageService();
        Assert.True(storage.InsertSecret(s_secrets[0], Bolt3AppendixDVectors.StorageIndexMax));
        await using (var writeContext = db.CreateDbContext())
        {
            await new RemoteShachainDbRepository(writeContext).SaveAsync(channelId, storage.Export());
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using (var deleteContext = db.CreateDbContext())
        {
            // The shachain rows are not tracked here: the database-level cascade must remove them
            var channel = await deleteContext.Channels.SingleAsync(c => c.ChannelId == channelId,
                                                                   TestContext.Current.CancellationToken);
            deleteContext.Channels.Remove(channel);
            await deleteContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = db.CreateDbContext();
        Assert.Empty(await new RemoteShachainDbRepository(readContext).GetByChannelIdAsync(channelId));
    }

    private static async Task<ChannelId> SeedChannelAsync(SqliteDbTestContext db)
    {
        var channel = SqliteDbTestContext.CreateChannel(true);

        await using var context = db.CreateDbContext();
        await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return channel.ChannelId;
    }
}