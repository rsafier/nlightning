using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// The CDK payment processor's quotes and deposits (NL-997, migration <c>AddCashuProcessorQuotes</c>) on the real
/// SQLite schema.
/// </summary>
public class CashuQuotePersistenceTests
{
    [Fact]
    public async Task Given_SchemaFromBeforeAddCashuProcessorQuotes_When_Migrated_Then_QuotesRoundTrip()
    {
        // Arrange (the SQLite run of the schema round trip the Postgres test shares)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await CashuQuoteSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }
}