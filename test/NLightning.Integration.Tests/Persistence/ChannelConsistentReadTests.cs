using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Interceptors;
using Infrastructure.Persistence.Providers;

/// <summary>
/// NL-810 on SQLite: a file database with one connection per context, as the daemon runs it, so a save from another
/// context really commits between two queries of a load (the in-memory test databases share one connection).
/// </summary>
public class ChannelConsistentReadTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ASpliceLockCommittingMidLoad_When_TheChannelsAreListed_Then_EachIsAllBeforeOrAllAfterTheLock(bool wal)
    {
        // Arrange
        var path = await CreateDatabaseFileAsync(wal);
        try
        {
            // Act & Assert
            await ChannelConsistentReadRoundTrip.AssertListedChannelIsConsistentAsync(
                interceptors => CreateContext(path, interceptors), TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ASpliceLockCommittingMidLoad_When_TheSignerLoadsTheChannel_Then_ItIsAllBeforeOrAllAfterTheLock(bool wal)
    {
        // Arrange
        var path = await CreateDatabaseFileAsync(wal);
        try
        {
            // Act & Assert
            await ChannelConsistentReadRoundTrip.AssertSigningInfoIsConsistentAsync(
                interceptors => CreateContext(path, interceptors), TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AModelFromBeforeASavedSpliceLock_When_ItsStateIsLoaded_Then_TheStoredFundingWins(bool wal)
    {
        // Arrange
        var path = await CreateDatabaseFileAsync(wal);
        try
        {
            // Act & Assert
            await ChannelConsistentReadRoundTrip.AssertStaleParamsFollowTheStoredFundingAsync(
                interceptors => CreateContext(path, interceptors), TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A database file name. With <paramref name="wal"/> EF creates the file at the migration and puts it in WAL mode
    /// (the daemon's databases); without, the file is created here in rollback-journal mode, where a writer's commit
    /// waits for the read transaction instead of committing beside its snapshot.
    /// </summary>
    private static async Task<string> CreateDatabaseFileAsync(bool wal)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nltg-nl810-{Guid.NewGuid():N}.db");
        if (wal)
            return path;

        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        Assert.Equal("delete", (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        return path;
    }

    private static NLightningDbContext CreateContext(string path, IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite($"Data Source={path};Pooling=False",
                                x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .AddInterceptors([new SqliteDurabilityInterceptor(), .. interceptors])
                     .Options;
        return new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }
}