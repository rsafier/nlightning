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
    public async Task Given_SqlServerRowsFromBeforeAddInvoicesPaymentsAndCircuits_When_Migrated_Then_TheyMoveForwardAndTheNewTablesRoundTrip()
    {
        // Arrange (ABCD W1-C: a snapshot saved before the migration still loads and takes an HTLC origin; invoices,
        // payments with their route and forward circuits round-trip on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_payment_schema");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await PaymentSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                 DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerChannelsFromBeforeAddChainWatchAndBroadcasts_When_Migrated_Then_FundingOutputsAreWatchedAndTheNewTablesRoundTrip()
    {
        // Arrange (BOLT 5 plan O0-T4: the funding-outpoint backfill runs on real rows; watched outpoints, broadcasts
        // and block headers round-trip on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_chain_watch");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await ChainWatchSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                    DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerRowsFromBeforeAddAttributionData_When_Migrated_Then_AttributionAndHoldTimesRoundTrip()
    {
        // Arrange (NL-326: rows written before the migration load with no attribution and no hold time; attributed
        // removals, receipt times and per-hop hold times round-trip on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_attribution");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await AttributionSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                     DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerChannelsFromBeforeAddGossipGraph_When_Migrated_Then_TheyArePrivateAndTheGraphRoundTrips()
    {
        // Arrange (BOLT 7 plan G2-T3/G1-T1: channels stored before the migration are private with no announcement
        // state and their signing data still loads (NL-067); the graph tables round-trip raw bytes on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_gossip_graph");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await GossipGraphSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                     DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerRowsFromBeforeAddOnionReplaySet_When_Migrated_Then_TheyMoveForwardAndTheReplaySetWorks()
    {
        // Arrange (NL-078: rows written before the migration move forward; replay entries round-trip, are pruned by
        // height, and the persistent store detects a replay across a restart on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_onion_replay");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await OnionReplaySchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                     DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerChannelsFromBeforeAddOnchainResolution_When_Migrated_Then_TheLogStartIsRecordedAndTheNewTablesRoundTrip()
    {
        // Arrange (BOLT 5 plan O1-T3: the revocation-log start is recorded for real rows; the revocation log, channel
        // closes, output resolutions and state 37 round-trip on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_onchain_resolution");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await OnchainResolutionSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                           DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
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