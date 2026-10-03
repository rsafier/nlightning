using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Migration <c>AddOnchainResolution</c> (BOLT 5 plan O1-T3) on SQLite: the revocation-log start of channels stored
/// with the older schema, and the round trips of the new tables (shared with the Docker Postgres/SQL Server tests
/// through <see cref="OnchainResolutionSchemaRoundTrip"/>).
/// </summary>
public class OnchainResolutionPersistenceTests
{
    [Fact]
    public async Task Given_ChannelsFromBeforeAddOnchainResolution_When_Migrated_Then_TheLogStartIsRecordedAndTheNewTablesRoundTrip()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (the same rows and assertions as the Docker Postgres/SQL Server tests)
        await OnchainResolutionSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }
}