using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Migration <c>AddChainWatchAndBroadcasts</c> (BOLT 5 plan O0-T4) on SQLite: the funding-output backfill on rows
/// written with the older schema, and the round trips of the new tables (shared with the Docker Postgres/SQL Server
/// tests through <see cref="ChainWatchSchemaRoundTrip"/>).
/// </summary>
public class ChainWatchPersistenceTests
{
    [Fact]
    public async Task Given_ChannelsFromBeforeAddChainWatchAndBroadcasts_When_Migrated_Then_FundingOutputsAreWatchedAndTheNewTablesRoundTrip()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (the same rows and assertions as the Docker Postgres/SQL Server tests)
        await ChainWatchSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }
}