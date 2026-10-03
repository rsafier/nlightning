using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Migration <c>AddGossipGraph</c> (BOLT 7 plan G2-T3 and the channel columns of G1-T1) on SQLite: the seeded upgrade
/// and the graph repository round trip of <see cref="GossipGraphSchemaRoundTrip"/>, which the Docker Postgres/SQL
/// Server tests run too.
/// </summary>
public class GossipGraphPersistenceTests
{
    [Fact]
    public async Task Given_ChannelsFromBeforeAddGossipGraph_When_Migrated_Then_TheyArePrivateAndTheGraphRoundTrips()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await GossipGraphSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_GraphChannelsFromBeforeAddGraphFundingTxId_When_Migrated_Then_TheyHaveNoTxIdAndItRoundTrips()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await GraphFundingTxIdSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }
}