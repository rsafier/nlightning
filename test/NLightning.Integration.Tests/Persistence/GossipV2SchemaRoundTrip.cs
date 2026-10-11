using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Gossip;
using GraphGossipVersions = Domain.Gossip.Graph.GraphGossipVersions;

/// <summary>
/// Provider-agnostic proof for migration <c>AddGossipV2</c> (taproot gossip, NL-878): graph rows stored before it are
/// BOLT 7 rows afterwards (version 1, both bitcoin keys); a v2-only channel without bitcoin keys, a
/// <c>channel_update_2</c> next to the <c>channel_update</c> of the same direction (the policy key gained the version),
/// its inbound fees, and a node with both announcements round-trip.
/// </summary>
internal static class GossipV2SchemaRoundTrip
{
    private const string MigrationName = "_AddGossipV2";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddGossipV2, with a BOLT 7 channel, one of its policies and a node
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
                           ("ReceivedAt", "638940000000000000")),
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
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("GraphNodes",
                           ("NodeId", "{0}"), ("Timestamp", "1758000002"), ("Features", "{1}"), ("Alias", "{2}"),
                           ("Color", "{3}"), ("Addresses", "{4}"), ("RawAnnouncement", "{5}"),
                           ("ReceivedAt", "638940000000000000")),
                [
                    (byte[])nodeId1, new byte[] { 0x02 }, new byte[32], new byte[] { 1, 2, 3 }, Array.Empty<byte>(),
                    new byte[] { 0x99 }
                ], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the migrated rows are BOLT 7 rows
        GraphNodeRecord migratedNode;
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            var channel = Assert.Single(await repository.GetChannelsAsync(cancellationToken));
            Assert.Equal(GraphGossipVersions.V1, channel.Versions);
            Assert.Null(channel.RawAnnouncement2);
            Assert.Equal(Key(0x41), channel.BitcoinKey1);
            Assert.Equal(raw, channel.RawAnnouncement);
            var policy = Assert.Single(await repository.GetPoliciesAsync(scid));
            Assert.Equal(1, policy.Version);
            Assert.Equal(0u, policy.InboundFeeBaseMsat);
            migratedNode = Assert.Single(await repository.GetNodesAsync(cancellationToken));
            Assert.Equal(GraphGossipVersions.V1, migratedNode.Versions);
            Assert.Null(migratedNode.BlockHeight);
            Assert.Null(migratedNode.RawAnnouncement2);
        }

        // Assert: a v2-only channel without bitcoin keys, a v2 policy next to the v1 one, a node with both
        var v2Scid = new ShortChannelId(812_346, 1, 0);
        var raw2 = Enumerable.Range(0, 300).Select(i => (byte)(0x10 + i)).ToArray();
        var v2Channel = new GraphChannelRecord(v2Scid, nodeId1, nodeId2, null, null, 50_000, [], [],
                                               GraphChannelVerification.Verified, null,
                                               new DateTimeOffset(638940000000000000, TimeSpan.Zero), null,
                                               GraphGossipVersions.V2, raw2);
        var v2Policy = new GraphPolicyRecord(scid, 1, 912_000, 0, 1, 80, 1, 25_000_000, 1_000, 1, [0x55], 2, 7, 8);
        var bothNode = new GraphNodeRecord(nodeId1, migratedNode.Timestamp, migratedNode.Features, migratedNode.Alias,
                                           migratedNode.Color, migratedNode.Addresses, migratedNode.RawAnnouncement,
                                           migratedNode.ReceivedAt)
        {
            Versions = GraphGossipVersions.V1 | GraphGossipVersions.V2,
            BlockHeight = 912_001,
            RawAnnouncement2 = [0x66, 0x67]
        };
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            await repository.UpsertChannelAsync(v2Channel);
            await repository.UpsertPoliciesAsync([v2Policy], cancellationToken);
            await repository.UpsertNodesAsync([bothNode], cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            var channel = await repository.GetChannelAsync(v2Scid);
            Assert.NotNull(channel);
            Assert.Null(channel.BitcoinKey1);
            Assert.Null(channel.BitcoinKey2);
            Assert.Equal(GraphGossipVersions.V2, channel.Versions);
            Assert.Equal(raw2, channel.RawAnnouncement2);
            Assert.Empty(channel.RawAnnouncement);

            var policies = await repository.GetPoliciesAsync(scid);
            Assert.Equal(2, policies.Count);
            var policy2 = Assert.Single(policies, p => p.Version == 2);
            Assert.Equal((912_000u, 7u, 8u, 25_000_000UL), (policy2.Timestamp, policy2.InboundFeeBaseMsat,
                                                             policy2.InboundFeeProportionalMillionths,
                                                             policy2.HtlcMaximumMsat));
            Assert.Equal(1758000001u, Assert.Single(policies, p => p.Version == 1).Timestamp);

            var node = Assert.Single(await repository.GetNodesAsync(cancellationToken));
            Assert.Equal(GraphGossipVersions.V1 | GraphGossipVersions.V2, node.Versions);
            Assert.Equal(912_001u, node.BlockHeight);
            Assert.Equal([0x66, 0x67], node.RawAnnouncement2);
            Assert.Equal([0x99], node.RawAnnouncement);
        }

        // Assert: deleting the channel removes both of its policies
        await using (var context = contextFactory())
        {
            await new GraphDbRepository(context).DeleteChannelsAsync([scid], cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
            Assert.Empty(await new GraphDbRepository(context).GetPoliciesAsync(scid));
    }

    private static CompactPubKey Key(byte seed)
    {
        var bytes = Enumerable.Repeat(seed, 33).ToArray();
        bytes[0] = 0x02;
        return bytes;
    }
}