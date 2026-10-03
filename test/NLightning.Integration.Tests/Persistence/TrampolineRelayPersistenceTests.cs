using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Trampoline relays, their parts and the payer side's trampoline hops (NL-875, migration <c>AddTrampolineRelays</c>)
/// on the real SQLite schema.
/// </summary>
public class TrampolineRelayPersistenceTests
{
    [Fact]
    public async Task Given_SchemaFromBeforeAddTrampolineRelays_When_Migrated_Then_RelaysRoundTrip()
    {
        // Arrange (the SQLite run of the schema round trip the Docker Postgres test shares)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await TrampolineRelaySchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }
}