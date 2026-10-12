using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Peer storage blobs (BOLT 1 <c>option_provide_storage</c>, migration <c>AddPeerStorage</c>) on the real SQLite schema.
/// </summary>
public class PeerStoragePersistenceTests
{
    [Fact]
    public async Task Given_SchemaFromBeforeAddPeerStorage_When_Migrated_Then_BlobsRoundTrip()
    {
        // Arrange (the SQLite run of the schema round trip the Docker Postgres/SQL Server tests share)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await PeerStorageSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SchemaFromBeforeAddPeerStorageRetrievals_When_Migrated_Then_RetrievalsRoundTrip()
    {
        // Arrange (NL-432; the SQLite run of the round trip the Docker Postgres test shares)
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert
        await PeerStorageRetrievalSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }
}