using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Liquidity purchases (NL-850 LA3, migration <c>AddLiquidityPurchases</c>) on the real SQLite schema.
/// </summary>
public class LiquidityPurchasePersistenceTests
{
    [Fact]
    public async Task Given_SchemaFromBeforeAddLiquidityPurchases_When_Migrated_Then_PurchasesRoundTrip()
    {
        // Arrange (the SQLite run of the schema round trip the Docker Postgres test shares)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await LiquidityPurchaseSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }
}