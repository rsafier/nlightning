using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Runs the channel data steps of the Sqlite migrations on rows written with the schema that came before them.
/// </summary>
public sealed class ChannelMigrationDataTests : IAsyncDisposable
{
    private const string BeforeSplitChannelParams = "20260925155346_AddChannelScidAliases";
    private const string BeforeMsatBalances = "20260925184642_SplitChannelParams";

    private static readonly byte[] s_channelId = Enumerable.Repeat((byte)0x07, 32).ToArray();

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    [Fact]
    public async Task Given_OneSidedConfigRow_When_SplitChannelParamsRuns_Then_BothSidesGetTheOldValues()
    {
        // Arrange
        await using var context = await CreateContextAtAsync(BeforeSplitChannelParams);
        await InsertChannelAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChannelConfigs" ("ChannelId", "MinimumDepth", "ToSelfDelay", "MaxAcceptedHtlcs",
                "LocalDustLimitAmountSats", "RemoteDustLimitAmountSats", "HtlcMinimumMsat", "ChannelReserveAmountSats",
                "MaxHtlcAmountInFlight", "FeeRatePerKwSatoshis", "OptionAnchorOutputs", "UseScidAlias")
            VALUES ({0}, 3, 144, 30, 354, 546, 1000, 10000, 800000000, 253, 0, 0)
            """, [s_channelId], TestContext.Current.CancellationToken);

        // Act
        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var config = await context.ChannelConfigs.AsNoTracking()
                                  .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(354, config.LocalDustLimitAmountSats);
        Assert.Equal(546, config.RemoteDustLimitAmountSats);
        Assert.Equal(10_000, config.LocalChannelReserveAmountSats);
        Assert.Equal(10_000, config.RemoteChannelReserveAmountSats);
        Assert.Equal(1_000UL, config.LocalHtlcMinimumMsat);
        Assert.Equal(1_000UL, config.RemoteHtlcMinimumMsat);
        Assert.Equal((ushort)30, config.LocalMaxAcceptedHtlcs);
        Assert.Equal((ushort)30, config.RemoteMaxAcceptedHtlcs);
        Assert.Equal(800_000_000UL, config.LocalMaxHtlcValueInFlightMsat);
        Assert.Equal(800_000_000UL, config.RemoteMaxHtlcValueInFlightMsat);
        Assert.Equal((ushort)144, config.LocalToSelfDelay);
        Assert.Equal((ushort)144, config.RemoteToSelfDelay);
    }

    [Fact]
    public async Task Given_ConfigRowFromBeforeTheSplit_When_Migrated_Then_ItIsMarkedAsInferred()
    {
        // Arrange (the split copies one set of values into both sides, which is only approximate: a non-initiator
        // announced the node's htlc_minimum_msat, and an initiator never stored the peer's limits)
        await using var context = await CreateContextAtAsync(BeforeSplitChannelParams);
        await InsertChannelAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChannelConfigs" ("ChannelId", "MinimumDepth", "ToSelfDelay", "MaxAcceptedHtlcs",
                "LocalDustLimitAmountSats", "RemoteDustLimitAmountSats", "HtlcMinimumMsat", "ChannelReserveAmountSats",
                "MaxHtlcAmountInFlight", "FeeRatePerKwSatoshis", "OptionAnchorOutputs", "UseScidAlias")
            VALUES ({0}, 3, 144, 30, 354, 546, 1000, 10000, 800000000, 253, 0, 0)
            """, [s_channelId], TestContext.Current.CancellationToken);

        // Act
        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var config = await context.ChannelConfigs.AsNoTracking()
                                  .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(config.HasInferredParams);
    }

    [Fact]
    public async Task Given_ConfigRowWithoutReserve_When_SplitChannelParamsRuns_Then_ReservesAreZero()
    {
        // Arrange
        await using var context = await CreateContextAtAsync(BeforeSplitChannelParams);
        await InsertChannelAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChannelConfigs" ("ChannelId", "MinimumDepth", "ToSelfDelay", "MaxAcceptedHtlcs",
                "LocalDustLimitAmountSats", "RemoteDustLimitAmountSats", "HtlcMinimumMsat", "ChannelReserveAmountSats",
                "MaxHtlcAmountInFlight", "FeeRatePerKwSatoshis", "OptionAnchorOutputs", "UseScidAlias")
            VALUES ({0}, 3, 144, 30, 354, 546, 1000, NULL, 800000000, 253, 0, 0)
            """, [s_channelId], TestContext.Current.CancellationToken);

        // Act
        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var config = await context.ChannelConfigs.AsNoTracking()
                                  .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, config.LocalChannelReserveAmountSats);
        Assert.Equal(0, config.RemoteChannelReserveAmountSats);
    }

    [Fact]
    public async Task Given_SatoshiBalances_When_StoreMsatBalancesRuns_Then_BalancesAreInMsat()
    {
        // Arrange (NL-191)
        await using var context = await CreateContextAtAsync(BeforeMsatBalances);
        await InsertChannelAsync(context);

        // Act
        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var channel = await context.Channels.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(600_000_000, channel.LocalBalanceMsat);
        Assert.Equal(400_000_000, channel.RemoteBalanceMsat);
        Assert.Null(channel.ShortChannelId);
    }

    [Fact]
    public async Task Given_ChannelWithoutHtlcsAndNextIdsOne_When_StoreMsatBalancesRuns_Then_NextIdsAreZero()
    {
        // Arrange (NL-190: HTLC ids start at 0)
        await using var context = await CreateContextAtAsync(BeforeMsatBalances);
        await InsertChannelAsync(context);

        // Act
        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var channel = await context.Channels.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0UL, channel.LocalNextHtlcId);
        Assert.Equal(0UL, channel.RemoteNextHtlcId);
    }

    [Fact]
    public async Task Given_ChannelWithAnHtlcRow_When_StoreMsatBalancesRuns_Then_NextIdsAreKept()
    {
        // Arrange
        await using var context = await CreateContextAtAsync(BeforeMsatBalances);
        await InsertChannelAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Htlcs" ("ChannelId", "HtlcId", "Direction", "AddMessageBytes", "AmountMsat", "CltvExpiry",
                "ObscuredCommitmentNumber", "PaymentHash", "State")
            VALUES ({0}, 0, 0, {1}, 1000, 500, 0, {2}, 0)
            """, [s_channelId, new byte[] { 0x00 }, new byte[32]], TestContext.Current.CancellationToken);

        // Act
        await context.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var channel = await context.Channels.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1UL, channel.LocalNextHtlcId);
        Assert.Equal(1UL, channel.RemoteNextHtlcId);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private async Task<NLightningDbContext> CreateContextAtAsync(string migration)
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(_connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        var context = new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
        await context.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);

        return context;
    }

    private static Task InsertChannelAsync(NLightningDbContext context)
    {
        var fundingTxId = new byte[32];
        var remoteNodeId = new byte[33];
        remoteNodeId[0] = 0x02;

        return context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Channels" ("ChannelId", "FundingCreatedAtBlockHeight", "FundingTxId", "FundingOutputIndex",
                "FundingAmountSatoshis", "IsInitiator", "RemoteNodeId", "LocalNextHtlcId", "RemoteNextHtlcId",
                "LocalRevocationNumber", "RemoteRevocationNumber", "State", "Version", "LocalBalanceSatoshis",
                "RemoteBalanceSatoshis")
            VALUES ({0}, 100, {1}, 0, 1000000, 1, {2}, 1, 1, 0, 0, 40, 1, '600000', '400000')
            """, [s_channelId, fundingTxId, remoteNodeId], TestContext.Current.CancellationToken);
    }
}