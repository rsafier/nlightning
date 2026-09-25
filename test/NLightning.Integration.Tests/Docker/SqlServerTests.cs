using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Docker;

using Fixtures;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Persistence;

[Collection("sqlserver")]
public class SqlServerTests
{
    private readonly SqlServerFixture _fixture;

    public SqlServerTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Given_SqlServerContainer_When_MigratingAndStoringPeer_Then_PeerRoundTrips()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlServer(_fixture.DbConnectionString,
                                   x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.SqlServer"))
                     .Options;
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // SqlServer takes a while to accept connections after the container starts
        await using (var context = new NLightningDbContext(options, databaseTypeProvider))
        {
            while (!await context.Database.CanConnectAsync(TestContext.Current.CancellationToken))
                await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // Act & Assert
        await PersistenceRoundTrip.AssertPeerRoundTripAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                            TestContext.Current.CancellationToken);
    }
}