using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// Provider-agnostic proof of the data step of migration <c>AddSpliceFundings</c> (splicing plan §3.8, SP1-C-T4),
/// run by the SQLite test and the Docker Postgres/SQL Server tests: the commitment slots and revocation-log rows of
/// channels stored before it move under their channel's funding txid, and every channel with a known funding outpoint
/// gets its <see cref="ChannelFundingKind.Initial"/>, <see cref="ChannelFundingStatus.Current"/> funding row with the
/// key sets' funding keys.
/// </summary>
internal static class SpliceFundingsSchemaRoundTrip
{
    private const string MigrationName = "_AddSpliceFundings";

    public static async Task AssertMigrationAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                                  CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddSpliceFundings; channel 0x51 funded (commitments and a revocation-log
        // row), 0x52 funded without a snapshot, 0x53 before funding_created (zero txid)
        var funded = ChainWatchSchemaRoundTrip.ChannelIdOf(0x51);
        var noSnapshot = ChainWatchSchemaRoundTrip.ChannelIdOf(0x52);
        var unfunded = ChainWatchSchemaRoundTrip.ChannelIdOf(0x53);
        var fundedTxId = ChainWatchSchemaRoundTrip.TxIdOf(0x61);
        var noSnapshotTxId = ChainWatchSchemaRoundTrip.TxIdOf(0x62);
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            await ChainWatchSchemaRoundTrip.SeedChannelAsync(context, databaseType, funded, fundedTxId, 1,
                                                             ChannelState.Open, cancellationToken, 4);
            await ChainWatchSchemaRoundTrip.SeedChannelAsync(context, databaseType, noSnapshot, noSnapshotTxId, 0,
                                                             ChannelState.V1FundingSigned, cancellationToken);
            await ChainWatchSchemaRoundTrip.SeedChannelAsync(context, databaseType, unfunded,
                                                             ChainWatchSchemaRoundTrip.TxIdOf(0x00), 0,
                                                             ChannelState.V1Opening, cancellationToken);
            foreach (var channelId in new[] { funded, noSnapshot, unfunded })
            {
                await SeedKeySetAsync(context, databaseType, channelId, true, 0x70, cancellationToken);
                await SeedKeySetAsync(context, databaseType, channelId, false, 0x80, cancellationToken);
            }

            await SeedCommitmentsAsync(context, databaseType, funded, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert
        await using (var context = contextFactory())
        {
            var commitments = await context.Commitments.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(3, commitments.Count);
            Assert.All(commitments, c => Assert.Equal(fundedTxId, c.FundingTxId));
            var revoked = Assert.Single(await context.RevokedCommitments.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(fundedTxId, revoked.FundingTxId);
            Assert.Equal(3UL, revoked.Number);

            var fundings = await context.ChannelFundings.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(2, fundings.Count);
            Assert.DoesNotContain(fundings, f => f.ChannelId == unfunded);
            foreach (var (channelId, txId, index) in new[] { (funded, fundedTxId, (ushort)1), (noSnapshot, noSnapshotTxId, (ushort)0) })
            {
                var row = Assert.Single(fundings, f => f.ChannelId == channelId);
                Assert.Equal(txId, row.FundingTxId);
                Assert.Equal(index, row.OutputIndex);
                Assert.Equal(1_000_000L, row.CapacitySatoshis);
                Assert.Equal(Key(0x70), (byte[])row.LocalFundingPubKey);
                Assert.Equal(Key(0x80), (byte[])row.RemoteFundingPubKey);
                Assert.Equal(0u, row.LocalFundingKeyIndex);
                Assert.Equal((byte)ChannelFundingKind.Initial, row.Kind);
                Assert.Equal((byte)ChannelFundingStatus.Current, row.Status);
                Assert.Equal(0, row.Sequence);
                Assert.Equal(0L, row.LocalBalanceDeltaMsat);
                Assert.False(row.SpliceLockedSent);
            }

            var channel = await context.Channels.AsNoTracking().SingleAsync(c => c.ChannelId == funded,
                                                                            cancellationToken);
            Assert.False(channel.IsDualFunded);
            Assert.Null(channel.LocalFundingContributionSatoshis);
            Assert.Empty(await context.ChannelPolicies.AsNoTracking().ToListAsync(cancellationToken));

            // The migrated rows are the state machine's slots: the funding repository reads the funding set
            var set = await new ChannelFundingDbRepository(context).GetFundingSetAsync(funded);
            Assert.NotNull(set);
            Assert.Equal(fundedTxId, set.Current.FundingTxId);
            Assert.Empty(set.Pending);
        }

        await AssertDownRefusedOnALockedSpliceAsync(contextFactory, databaseType, funded, fundedTxId, cancellationToken);
    }

    /// <summary>
    /// The hand-written guard of the migration's Down step: while a channel runs on a locked splice (its current funding
    /// is not the initial one, and its rotated funding keys live only in <c>ChannelFundings</c>) the rollback is refused
    /// and nothing is dropped; once no such channel is left it rolls back, and the migration applies again.
    /// </summary>
    private static async Task AssertDownRefusedOnALockedSpliceAsync(Func<NLightningDbContext> contextFactory,
                                                                    DatabaseType databaseType, ChannelId channelId,
                                                                    TxId initialTxId,
                                                                    CancellationToken cancellationToken)
    {
        var spliceTxId = ChainWatchSchemaRoundTrip.TxIdOf(0x6a);
        var sql = new MigrationSqlDialect(databaseType);
        await using (var context = contextFactory())
        {
            await context.Database.ExecuteSqlRawAsync(
                sql.Update("ChannelFundings", ("Status", $"{(byte)ChannelFundingStatus.Replaced}"),
                           ("ChannelId", "{0}"), ("FundingTxId", "{1}")),
                [(byte[])channelId, (byte[])initialTxId], cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("ChannelFundings",
                           ("ChannelId", "{0}"), ("FundingTxId", "{1}"), ("OutputIndex", "0"),
                           ("CapacitySatoshis", "1200000"), ("LocalFundingPubKey", "{2}"),
                           ("RemoteFundingPubKey", "{3}"), ("LocalFundingKeyIndex", "1"),
                           ("LocalBalanceDeltaMsat", "200000000"), ("RemoteBalanceDeltaMsat", "0"),
                           ("Kind", $"{(byte)ChannelFundingKind.Splice}"),
                           ("Status", $"{(byte)ChannelFundingStatus.Current}"),
                           ("SpliceLockedSent", sql.Bool(true)), ("SpliceLockedReceived", sql.Bool(true)),
                           ("AnnouncementSignaturesReceived", sql.Bool(false)), ("Sequence", "1")),
                [(byte[])channelId, (byte[])spliceTxId, Key(0x71), Key(0x81)], cancellationToken);
        }

        string previous;
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            previous = migrations[migrations.IndexOf(target) - 1];

            // Act
            var refusal = await Record.ExceptionAsync(() => context.GetService<IMigrator>()
                                                                     .MigrateAsync(previous, cancellationToken));

            // Assert
            Assert.NotNull(refusal);
        }

        await using (var context = contextFactory())
        {
            // The later migrations roll back first, each in its own transaction; the refusal keeps this one applied.
            // Bring them back before reading through the current model
            Assert.DoesNotContain(await context.Database.GetPendingMigrationsAsync(cancellationToken),
                                  m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
            Assert.Equal(2, await context.ChannelFundings.CountAsync(f => f.ChannelId == channelId,
                                                                     cancellationToken));
            Assert.NotEmpty(await context.Commitments.AsNoTracking().ToListAsync(cancellationToken));

            // Without a locked splice the rollback runs, and the migration applies again
            await context.Database.ExecuteSqlRawAsync(
                sql.Delete("ChannelFundings", ("ChannelId", "{0}"), ("FundingTxId", "{1}")),
                [(byte[])channelId, (byte[])spliceTxId], cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                sql.Update("ChannelFundings", ("Status", $"{(byte)ChannelFundingStatus.Current}"),
                           ("ChannelId", "{0}"), ("FundingTxId", "{1}")),
                [(byte[])channelId, (byte[])initialTxId], cancellationToken);

            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(previous, cancellationToken);
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }
    }

    internal static byte[] Key(byte seed)
    {
        var key = new byte[33];
        key[0] = 0x02;
        key[32] = seed;
        return key;
    }

    private static async Task SeedKeySetAsync(NLightningDbContext context, DatabaseType databaseType,
                                              ChannelId channelId, bool isLocal, byte seed,
                                              CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("ChannelKeySets",
                       ("ChannelId", "{0}"), ("IsLocal", sql.Bool(isLocal)), ("FundingPubKey", "{1}"),
                       ("RevocationBasepoint", "{2}"), ("PaymentBasepoint", "{2}"),
                       ("DelayedPaymentBasepoint", "{2}"), ("HtlcBasepoint", "{2}"),
                       ("CurrentPerCommitmentIndex", "281474976710655"), ("CurrentPerCommitmentPoint", "{2}"),
                       ("KeyIndex", "0")),
            [(byte[])channelId, Key(seed), Key((byte)(seed + 1))], cancellationToken);
    }

    private static async Task SeedCommitmentsAsync(NLightningDbContext context, DatabaseType databaseType,
                                                   ChannelId channelId, CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        var point = Key(0x90);
        foreach (var (slot, withPoint) in new[] { (0, false), (1, true), (2, true) })
        {
            var values = new List<(string, string)>
            {
                ("ChannelId", "{0}"), ("Slot", $"{slot}"), ("Number", "4"), ("FeeratePerKw", "253"),
                ("LocalMsat", "600000000"), ("RemoteMsat", "400000000"), ("Htlcs", "{1}")
            };
            if (withPoint)
                values.Add(("PerCommitmentPoint", "{2}"));
            await context.Database.ExecuteSqlRawAsync(sql.Insert("Commitments", values.ToArray()),
                                                      [(byte[])channelId, Array.Empty<byte>(), point],
                                                      cancellationToken);
        }

        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("RevokedCommitments",
                       ("ChannelId", "{0}"), ("Number", "3"), ("FeeratePerKw", "253"), ("LocalMsat", "600000000"),
                       ("RemoteMsat", "400000000"), ("Htlcs", "{1}")),
            [(byte[])channelId, new byte[53]], cancellationToken);
    }
}