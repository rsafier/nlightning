using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Gossip.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Gossip;

/// <summary>
/// Provider-agnostic proof for migration <c>AddGraphFundingTxId</c> (NL-352), shared by the SQLite test and the Docker
/// Postgres/SQL Server tests: a graph channel stored before the migration keeps every field and its policy and reads
/// back without a funding txid (so the pruner looks it up once more); afterwards the txid round-trips, is replaced and
/// cleared by an upsert.
/// </summary>
internal static class GraphFundingTxIdSchemaRoundTrip
{
    private const string MigrationName = "_AddGraphFundingTxId";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddGraphFundingTxId, with one graph channel and one of its policies
        var scid = new ShortChannelId(812_345, 678, 1);
        var nodeId1 = Key(0x31);
        var nodeId2 = Key(0x32);
        var raw = Enumerable.Range(0, 430).Select(i => (byte)(0x74 + i)).ToArray();
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            var sql = new MigrationSqlDialect(databaseType);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("GraphChannels",
                           ("ShortChannelId", "{0}"), ("NodeId1", "{1}"), ("NodeId2", "{2}"), ("BitcoinKey1", "{3}"),
                           ("BitcoinKey2", "{4}"), ("CapacitySat", "16777215"), ("Features", "{5}"),
                           ("RawAnnouncement", "{6}"), ("Verification", $"{(byte)GraphChannelVerification.Verified}"),
                           ("SpentAtHeight", "800"), ("ReceivedAt", "638940000000000000")),
                [
                    (byte[])scid, (byte[])nodeId1, (byte[])nodeId2, (byte[])Key(0x41), (byte[])Key(0x42),
                    new byte[] { 0x01, 0x00 }, raw
                ], cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("GraphChannelPolicies",
                           ("ShortChannelId", "{0}"), ("Direction", "1"), ("Timestamp", "1758000001"),
                           ("MessageFlags", "1"), ("ChannelFlags", "1"), ("CltvExpiryDelta", "40"),
                           ("HtlcMinimumMsat", sql.Decimal(1_000)), ("HtlcMaximumMsat", sql.Decimal(990_000_000)),
                           ("FeeBaseMsat", "1000"), ("FeePpm", "100"), ("RawUpdate", "{1}")),
                [(byte[])scid, new byte[] { 0x76, 0x77 }], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the migrated row is unchanged and has no funding txid; its policy is still there
        GraphChannelRecord migrated;
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            migrated = Assert.Single(await repository.GetChannelsAsync(cancellationToken));
            Assert.Equal(scid, migrated.ShortChannelId);
            Assert.Equal(nodeId1, migrated.NodeId1);
            Assert.Equal(nodeId2, migrated.NodeId2);
            Assert.Equal(16_777_215UL, migrated.CapacitySat);
            Assert.Equal(raw, migrated.RawAnnouncement);
            Assert.Equal(GraphChannelVerification.Verified, migrated.Verification);
            Assert.Equal(800U, migrated.SpentAtHeight);
            Assert.Equal(638940000000000000L, migrated.ReceivedAt.UtcTicks);
            Assert.Null(migrated.FundingTxId);
            var policy = Assert.Single(await repository.GetPoliciesAsync(scid));
            Assert.Equal(990_000_000UL, policy.HtlcMaximumMsat);
        }

        // Assert: the txid round-trips through an upsert (the pruner's lookup, NL-352), is replaced, then cleared
        var fundingTxId = ChainWatchSchemaRoundTrip.TxIdOf(0x65);
        var otherTxId = ChainWatchSchemaRoundTrip.TxIdOf(0x66);
        foreach (var expected in new[] { fundingTxId, otherTxId, (Domain.Bitcoin.ValueObjects.TxId?)null })
        {
            await using (var context = contextFactory())
            {
                await new GraphDbRepository(context).UpsertChannelAsync(migrated with { FundingTxId = expected });
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = contextFactory())
            {
                var repository = new GraphDbRepository(context);
                Assert.Equal(expected, (await repository.GetChannelAsync(scid))!.FundingTxId);
                Assert.Single(await repository.GetPoliciesAsync(scid));
            }
        }
    }

    private static Domain.Crypto.ValueObjects.CompactPubKey Key(byte seed)
    {
        var bytes = Enumerable.Repeat(seed, 33).ToArray();
        bytes[0] = 0x02;
        return bytes;
    }
}