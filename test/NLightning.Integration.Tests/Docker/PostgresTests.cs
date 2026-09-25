using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Docker;

using Fixtures;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Persistence;

[Collection("postgres")]
public class PostgresTests
{
    private readonly PostgresFixture _fixture;

    public PostgresTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Given_PostgresContainer_When_MigratingAndStoringPeer_Then_PeerRoundTrips()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseNpgsql(_fixture.DbConnectionString,
                                x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Postgres"))
                     .UseSnakeCaseNamingConvention()
                     .Options;
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.PostgreSql);

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