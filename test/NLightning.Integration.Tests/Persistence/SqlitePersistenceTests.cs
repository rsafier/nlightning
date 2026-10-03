using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

// Revived from the commented-out Docker/SqliteTests.cs; an in-memory database needs no Docker, so it runs in CI.
public class SqlitePersistenceTests
{
    [Fact]
    public async Task Given_InMemorySqlite_When_MigratingAndStoringPeer_Then_PeerRoundTrips()
    {
        // Arrange - the in-memory database lives as long as this open connection
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection,
                                x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.Sqlite);

        // Act & Assert
        await PersistenceRoundTrip.AssertPeerRoundTripAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                            TestContext.Current.CancellationToken);
    }
}