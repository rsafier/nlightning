using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;

/// <summary>
/// Provider-agnostic proof for the hand-written data steps of <c>PersistCommitmentNumbers</c>,
/// <c>SplitChannelParams</c>, <c>StoreMsatBalancesAndShortChannelId</c> and <c>FlagInferredChannelParams</c> (NL-237):
/// channel, config and HTLC rows written with the schema before the first of them are migrated to the latest schema.
/// Shared by the SQLite test and the Docker Postgres/SQL Server tests.
/// </summary>
internal static class LegacyChannelMigrationRoundTrip
{
    /// <summary>Has an HTLC row, so its next HTLC ids stay 1/1 (NL-190).</summary>
    private static readonly byte[] s_withHtlcChannelId = Enumerable.Repeat((byte)0x21, 32).ToArray();

    /// <summary>Next ids 1/1 and no HTLC row: reset to 0/0 (NL-190); a balance past 32 bits in msat (NL-191).</summary>
    private static readonly byte[] s_idleChannelId = Enumerable.Repeat((byte)0x22, 32).ToArray();

    /// <summary>Next ids other than 1/1 and no HTLC row: kept.</summary>
    private static readonly byte[] s_usedChannelId = Enumerable.Repeat((byte)0x23, 32).ToArray();

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before PersistCommitmentNumbers, with rows in it
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var first = migrations.Single(m => m.EndsWith("_PersistCommitmentNumbers", StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(first) - 1], cancellationToken);
            await SeedAsync(context, new MigrationSqlDialect(databaseType), cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert
        await using (var context = contextFactory())
        {
            var withHtlcId = new ChannelId(s_withHtlcChannelId);
            var idleId = new ChannelId(s_idleChannelId);
            var usedId = new ChannelId(s_usedChannelId);
            var channels = await context.Channels.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(3, channels.Count);
            var withHtlc = channels.Single(c => c.ChannelId == withHtlcId);
            var idle = channels.Single(c => c.ChannelId == idleId);
            var used = channels.Single(c => c.ChannelId == usedId);

            // PersistCommitmentNumbers: the commitment numbers start from the revocation numbers (NL-188)
            Assert.Equal(5UL, withHtlc.LocalCommitmentNumber);
            Assert.Equal(4UL, withHtlc.RemoteCommitmentNumber);
            Assert.Equal(5UL, withHtlc.LocalRevocationNumber);
            Assert.Equal(4UL, withHtlc.RemoteRevocationNumber);
            Assert.Equal(0UL, idle.LocalCommitmentNumber);
            Assert.Equal(7UL, used.LocalCommitmentNumber);
            Assert.Equal(6UL, used.RemoteCommitmentNumber);

            // StoreMsatBalancesAndShortChannelId: balances in msat (NL-191), next HTLC ids from 0 (NL-190)
            Assert.Equal(600_000_000, withHtlc.LocalBalanceMsat);
            Assert.Equal(400_000_000, withHtlc.RemoteBalanceMsat);
            Assert.Equal(5_000_000_000_000, idle.LocalBalanceMsat);
            Assert.Equal(123_000, idle.RemoteBalanceMsat);
            Assert.All(channels, c => Assert.Null(c.ShortChannelId));
            Assert.Equal((1UL, 1UL), (withHtlc.LocalNextHtlcId, withHtlc.RemoteNextHtlcId));
            Assert.Equal((0UL, 0UL), (idle.LocalNextHtlcId, idle.RemoteNextHtlcId));
            Assert.Equal((3UL, 2UL), (used.LocalNextHtlcId, used.RemoteNextHtlcId));

            // SplitChannelParams copies the one set of limits to both sides (NL-194), FlagInferredChannelParams marks it
            var configs = await context.ChannelConfigs.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(2, configs.Count);
            var withHtlcConfig = configs.Single(c => c.ChannelId == withHtlcId);
            Assert.Equal(354, withHtlcConfig.LocalDustLimitAmountSats);
            Assert.Equal(546, withHtlcConfig.RemoteDustLimitAmountSats);
            Assert.Equal(10_000, withHtlcConfig.LocalChannelReserveAmountSats);
            Assert.Equal(10_000, withHtlcConfig.RemoteChannelReserveAmountSats);
            Assert.Equal(1_000UL, withHtlcConfig.LocalHtlcMinimumMsat);
            Assert.Equal(1_000UL, withHtlcConfig.RemoteHtlcMinimumMsat);
            Assert.Equal((ushort)30, withHtlcConfig.LocalMaxAcceptedHtlcs);
            Assert.Equal((ushort)30, withHtlcConfig.RemoteMaxAcceptedHtlcs);
            Assert.Equal(800_000_000UL, withHtlcConfig.LocalMaxHtlcValueInFlightMsat);
            Assert.Equal(800_000_000UL, withHtlcConfig.RemoteMaxHtlcValueInFlightMsat);
            Assert.Equal((ushort)144, withHtlcConfig.LocalToSelfDelay);
            Assert.Equal((ushort)144, withHtlcConfig.RemoteToSelfDelay);
            Assert.False(withHtlcConfig.OptionAnchorOutputs);

            var idleConfig = configs.Single(c => c.ChannelId == idleId);
            Assert.Equal(0, idleConfig.LocalChannelReserveAmountSats);
            Assert.Equal(0, idleConfig.RemoteChannelReserveAmountSats);
            Assert.Equal(1UL, idleConfig.LocalHtlcMinimumMsat);
            Assert.Equal(1UL, idleConfig.RemoteHtlcMinimumMsat);
            Assert.Equal((ushort)483, idleConfig.LocalMaxAcceptedHtlcs);
            Assert.Equal((ushort)483, idleConfig.RemoteMaxAcceptedHtlcs);
            Assert.Equal(5_000_000_000_000UL, idleConfig.LocalMaxHtlcValueInFlightMsat);
            Assert.Equal(5_000_000_000_000UL, idleConfig.RemoteMaxHtlcValueInFlightMsat);
            Assert.Equal((ushort)720, idleConfig.LocalToSelfDelay);
            Assert.Equal((ushort)720, idleConfig.RemoteToSelfDelay);
            Assert.True(idleConfig.OptionAnchorOutputs);
            Assert.All(configs, c => Assert.True(c.HasInferredParams));

            // The HTLC row survives with its legacy state (refused on load, NL-025)
            var htlc = await context.Htlcs.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal(withHtlcId, htlc.ChannelId);
            Assert.Equal(0, htlc.State);
            Assert.Equal(1_000UL, htlc.AmountMsat);
            Assert.Empty(htlc.OnionRoutingPacket);
        }
    }

    private static async Task SeedAsync(NLightningDbContext context, MigrationSqlDialect sql,
                                        CancellationToken cancellationToken)
    {
        var remoteNodeId = new byte[33];
        remoteNodeId[0] = 0x02;

        foreach (var (channelId, isInitiator, nextIds, revocations, balances) in new[]
                 {
                     (s_withHtlcChannelId, true, (1, 1), (5, 4), (600_000UL, 400_000UL)),
                     (s_idleChannelId, false, (1, 1), (0, 0), (5_000_000_000UL, 123UL)),
                     (s_usedChannelId, true, (3, 2), (7, 6), (1UL, 2UL))
                 })
        {
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("Channels",
                           ("ChannelId", "{0}"), ("FundingCreatedAtBlockHeight", "100"), ("FundingTxId", "{1}"),
                           ("FundingOutputIndex", "0"), ("FundingAmountSatoshis", "6000000"),
                           ("IsInitiator", sql.Bool(isInitiator)), ("RemoteNodeId", "{2}"),
                           ("LocalNextHtlcId", $"{nextIds.Item1}"), ("RemoteNextHtlcId", $"{nextIds.Item2}"),
                           ("LocalRevocationNumber", $"{revocations.Item1}"),
                           ("RemoteRevocationNumber", $"{revocations.Item2}"), ("State", "40"), ("Version", "1"),
                           ("LocalBalanceSatoshis", sql.Decimal(balances.Item1)),
                           ("RemoteBalanceSatoshis", sql.Decimal(balances.Item2))),
                [channelId, new byte[32], remoteNodeId], cancellationToken);
        }

        foreach (var (channelId, toSelfDelay, maxAccepted, htlcMinimum, reserve, maxInFlight, anchors) in new[]
                 {
                     (s_withHtlcChannelId, 144, 30, 1_000UL, "10000", 800_000_000UL, false),
                     (s_idleChannelId, 720, 483, 1UL, "NULL", 5_000_000_000_000UL, true)
                 })
        {
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("ChannelConfigs",
                           ("ChannelId", "{0}"), ("MinimumDepth", "3"), ("ToSelfDelay", $"{toSelfDelay}"),
                           ("MaxAcceptedHtlcs", $"{maxAccepted}"), ("LocalDustLimitAmountSats", "354"),
                           ("RemoteDustLimitAmountSats", "546"), ("HtlcMinimumMsat", $"{htlcMinimum}"),
                           ("ChannelReserveAmountSats", reserve), ("MaxHtlcAmountInFlight", $"{maxInFlight}"),
                           ("FeeRatePerKwSatoshis", "253"), ("OptionAnchorOutputs", sql.Bool(anchors)),
                           ("UseScidAlias", "0")),
                [channelId], cancellationToken);
        }

        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Htlcs",
                       ("ChannelId", "{0}"), ("HtlcId", "0"), ("Direction", "0"), ("AddMessageBytes", "{1}"),
                       ("AmountMsat", "1000"), ("CltvExpiry", "500"), ("ObscuredCommitmentNumber", "0"),
                       ("PaymentHash", "{2}"), ("State", "0")),
            [s_withHtlcChannelId, new byte[] { 0x00, 0x80, 0x01 }, new byte[32]], cancellationToken);
    }
}