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
[Trait("Database", "SqlServer")]
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
    public async Task Given_SqlServerChannelsFromBeforeAddSpliceFundings_When_Migrated_Then_RowsMoveUnderTheirFundingTxIdAndDownRefusesALockedSplice()
    {
        // Arrange (splicing plan SP1-C-T4: the provider's own hand-written data step and Down guard run on a real
        // server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_splice_fundings");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await SpliceFundingsSchemaRoundTrip.AssertMigrationAsync(() => new NLightningDbContext(options,
                                                                                               databaseTypeProvider),
                                                                 DatabaseType.MicrosoftSql,
                                                                 TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerRowsOfAddSpliceHardening_When_RolledBack_Then_RefusedWhileASignedOnRecordExistsAndInboundOnlyPeersDropped()
    {
        // Arrange (wave spr review: the provider's own hand-written Down guard and data step run on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_splice_hardening");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await SpliceHardeningSchemaRoundTrip.AssertDownAsync(() => new NLightningDbContext(options,
                                                                                           databaseTypeProvider),
                                                             DatabaseType.MicrosoftSql,
                                                             TestContext.Current.CancellationToken);
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
    public async Task Given_SqlServerBooksFromBeforeAddAccountingFinancial_When_MigratedAndRolledBack_Then_TheyKeepTheirValues()
    {
        // Arrange (NL-602 A3-T0: A2's books move into the operational book, the financial tables round-trip and the
        // rollback keeps the operational book, on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_accounting_financial");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await AccountingFinancialSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options,
                                                                                           databaseTypeProvider),
                                                             DatabaseType.MicrosoftSql,
                                                             TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerRowsFromBeforeAddPaymentCustomRecords_When_Migrated_Then_KeysendRecordsMoveIntoTheirColumn()
    {
        // Arrange (NL-460: the keysend rows' custom records move out of the borrowed BOLT 12 invoice bytes and then
        // round-trip, on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_keysend_records");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await KeysendSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
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
    public async Task Given_SqlServerGraphChannelsFromBeforeAddGraphFundingTxId_When_Migrated_Then_TheyHaveNoTxIdAndItRoundTrips()
    {
        // Arrange (NL-352: a graph channel stored before the migration keeps its fields and policy with no funding
        // txid; the txid round-trips, is replaced and cleared on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_graph_funding_txid");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await GraphFundingTxIdSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
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
    public async Task Given_SqlServerSchemaFromBeforeAddBolt12Offers_When_Migrated_Then_Bolt11RowsKeptAndBolt12RowsRoundTrip()
    {
        // Arrange (NL-447, BOLT 12 plan B2: BOLT 11 rows keep Kind 0 and their string; offers, BOLT 12 invoices and
        // payments round-trip, and the invoice counts work, on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_bolt12_offers");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await Bolt12SchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                DatabaseType.MicrosoftSql, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerSchemaFromBeforeAddPeerStorage_When_Migrated_Then_BlobsRoundTrip()
    {
        // Arrange (BOLT 1 option_provide_storage: a 65531-byte blob round-trips, replaced and deleted, on a real
        // server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_peer_storage");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await PeerStorageSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                     TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerSchemaFromBeforeAddFeeInputReservations_When_Migrated_Then_ReservationsRoundTrip()
    {
        // Arrange (BOLT 5 plan O7-T1: fee input reservations round-trip, the outpoint key refuses a second
        // reservation and a delete cascades, on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_fee_input_reservations");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await FeeInputReservationSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, databaseTypeProvider), TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task Given_SqlServerSchema_When_AddressesAreIssuedAndBroadcastsAbandoned_Then_TheSharedRoundTripHolds()
    {
        // Arrange (NL-280: reserved and funded addresses never handed out again; NL-294: abandoned broadcasts listed)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_wallet_issuance");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await WalletIssuanceSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, databaseTypeProvider),
                                                        TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerFinancialBook_When_ResetToTheClose_Then_TheBulkStatementsHoldWithALateFact()
    {
        // Arrange (NL-662, NL-671: the correlated ExecuteDelete, the ExecuteUpdate of RemainingMsat + n, the late facts'
        // List.Contains and the flag test of ResetToCloseAsync, in its own transaction, on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_accounting_reset");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await AccountingBulkStatementsRoundTrip.AssertResetToCloseAsync(
            () => new NLightningDbContext(options, databaseTypeProvider), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_SqlServerFinancialBook_When_RolledBack_Then_TheBulkStatementsHoldWithALateFact()
    {
        // Arrange (NL-662, NL-671: the same bulk statements in RollbackOpenEntriesAsync, on a real server)
        var options = await CreateOwnDatabaseOptionsAsync("nltg_accounting_rollback");
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.MicrosoftSql);

        // Act & Assert
        await AccountingBulkStatementsRoundTrip.AssertRollbackAsync(
            () => new NLightningDbContext(options, databaseTypeProvider), TestContext.Current.CancellationToken);
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