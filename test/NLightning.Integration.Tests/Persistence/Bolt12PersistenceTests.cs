using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// BOLT 12 offers, invoices and payments (migration <c>AddBolt12Offers</c>, NL-447) on the real SQLite schema.
/// </summary>
public class Bolt12PersistenceTests
{
    [Fact]
    public async Task Given_SchemaFromBeforeAddBolt12Offers_When_Migrated_Then_Bolt11RowsKeptAndBolt12RowsRoundTrip()
    {
        // Arrange (the SQLite run of the schema round trip the Docker Postgres/SQL Server tests share)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await Bolt12SchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            DatabaseType.Sqlite, TestContext.Current.CancellationToken);
    }
}