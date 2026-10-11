using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Migration <c>AddAttributionData</c> on SQLite (NL-326; the same assertions run on Postgres and SQL Server in
/// <c>Docker/PostgresTests</c> and <c>Docker/SqlServerTests</c>).
/// </summary>
public class AttributionPersistenceTests
{
    [Fact]
    public async Task Given_RowsFromBeforeAddAttributionData_When_Migrated_Then_AttributionAndHoldTimesRoundTrip()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await AttributionSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }
}