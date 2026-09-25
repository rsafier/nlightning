using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

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

    [Fact]
    public async Task Given_SqlServerRowsFromBeforeAddCommitmentState_When_Migrated_Then_TheyMoveForwardAndStateRoundTrips()
    {
        // Arrange (NL-237: the data steps run on real rows; a database of its own, since the other tests migrate
        // theirs to the latest schema)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_commitment_state");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await CommitmentStateMigrationRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                            DatabaseType.MicrosoftSql,
                                                            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerRowsFromBeforePersistCommitmentNumbers_When_Migrated_Then_EveryChannelDataStepRuns()
    {
        // Arrange (NL-237: the data steps of PersistCommitmentNumbers, SplitChannelParams,
        // StoreMsatBalancesAndShortChannelId and FlagInferredChannelParams run on real rows)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_legacy_channels");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await LegacyChannelMigrationRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                          DatabaseType.MicrosoftSql,
                                                          TestContext.Current.CancellationToken);
    }

    /// <summary>Options for a database of its own on the container's server, once the server accepts
    /// connections.</summary>
    private async Task<DbContextOptions<NLightningDbContext>> CreateOwnDatabaseOptionsAsync(string database)
    {
        var connectionString = _fixture.DbConnectionString!.Replace("Database=tempdb", $"Database={database}",
                                                                     StringComparison.Ordinal);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlServer(connectionString,
                                   x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.SqlServer"))
                     .Options;

        // SqlServer takes a while to accept connections after the container starts
        await using var context = new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.MicrosoftSql));
        while (!await CanReachServerAsync(context))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        return options;
    }

    /// <summary>The database does not exist yet, so ask the server (master) instead of the database.</summary>
    private static async Task<bool> CanReachServerAsync(NLightningDbContext context)
    {
        try
        {
            await context.GetService<IRelationalDatabaseCreator>().ExistsAsync(TestContext.Current.CancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}