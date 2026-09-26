using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Persistence;
using Domain.Onchain.Enums;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Gossip;

/// <summary>
/// Provider-agnostic proof for migration <c>AddGossipGraph</c> (BOLT 7 plan G2-T3 and the channel columns of G1-T1),
/// shared by the SQLite test and the Docker Postgres/SQL Server tests: channels stored before the migration are private
/// (<c>AnnounceChannel</c> false) with no announcement state, the signer can still load their signing data (NL-067), and
/// the graph tables round-trip through <see cref="GraphDbRepository"/> with their raw bytes intact.
/// </summary>
internal static class GossipGraphSchemaRoundTrip
{
    private const string MigrationName = "_AddGossipGraph";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddGossipGraph, with a channel (config, both key sets) whose commitment was
        // signed for broadcast
        var channelId = ChainWatchSchemaRoundTrip.ChannelIdOf(0x61);
        var fundingTxId = ChainWatchSchemaRoundTrip.TxIdOf(0x62);
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            await ChainWatchSchemaRoundTrip.SeedChannelAsync(context, databaseType, channelId, fundingTxId, 2,
                                                             ChannelState.Open, cancellationToken);
            await SeedConfigAndKeySetsAsync(context, databaseType, channelId, cancellationToken);
            await SeedLocalCommitmentBroadcastAsync(context, databaseType, channelId, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the channel is private, with no announcement state; the graph is empty
        await using (var context = contextFactory())
        {
            var config = await context.ChannelConfigs.AsNoTracking().SingleAsync(cancellationToken);
            Assert.False(config.AnnounceChannel);
            var channel = await context.Channels.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Null(channel.RemoteAnnouncementNodeSig);
            Assert.Null(channel.RemoteAnnouncementBitcoinSig);
            Assert.Null(channel.LocalAnnouncementSigsSentAt);
            Assert.Empty(await context.GraphNodes.ToListAsync(cancellationToken));
            Assert.Empty(await context.GraphChannels.ToListAsync(cancellationToken));
            Assert.Empty(await context.GraphChannelPolicies.ToListAsync(cancellationToken));
            Assert.Empty(await context.GraphBannedNodes.ToListAsync(cancellationToken));
        }

        // Assert: the signer's loader reads the migrated channel (NL-067)
        await using (var context = contextFactory())
        {
            var signingInfo = await new ChannelSigningInfoDbRepository(context).GetAsync(channelId);
            Assert.NotNull(signingInfo);
            Assert.Equal(fundingTxId, signingInfo.Value.FundingTxId);
            Assert.Equal((ushort)2, signingInfo.Value.FundingOutputIndex);
            Assert.Equal(1_000_000_000UL, signingInfo.Value.FundingSatoshis);
            Assert.Equal(Key(0x11), signingInfo.Value.LocalFundingPubKey);
            Assert.Equal(Key(0x21), signingInfo.Value.RemoteFundingPubKey);
            Assert.Equal(Key(0x25), signingInfo.Value.RemoteHtlcBasepoint);
            Assert.Equal(7U, signingInfo.Value.ChannelKeyIndex);
            Assert.Equal(4UL, signingInfo.Value.BroadcastSignedCommitmentNumber);
            Assert.Null(signingInfo.Value.ShortChannelId);
            Assert.Null(await new ChannelSigningInfoDbRepository(context).GetAsync(
                            ChainWatchSchemaRoundTrip.ChannelIdOf(0x6F)));
        }

        // Assert: the new tables round-trip on this provider
        await AssertTablesRoundTripAsync(contextFactory, cancellationToken);
    }

    /// <summary>
    /// Every <see cref="GraphDbRepository"/> method, each write saved and read back from a fresh context: raw bytes
    /// byte-exact (with unknown trailing bytes), extreme values, replacement, spends and their reorg, cascade deletes
    /// and bans.
    /// </summary>
    public static async Task AssertTablesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                        CancellationToken cancellationToken)
    {
        var scidA = new ShortChannelId(812_345, 678, 1);
        var scidB = new ShortChannelId(ShortChannelId.MaxThreeByteValue, ShortChannelId.MaxThreeByteValue,
                                       ushort.MaxValue);
        var receivedAt = new DateTimeOffset(2026, 9, 26, 7, 8, 9, TimeSpan.FromHours(-5)).AddTicks(3_210);

        // Nodes: add, replace, list, delete
        var node = new GraphNodeRecord(Key(0x31), 1_758_000_000, [0x08, 0x00, 0x00, 0x02],
                                       Enumerable.Repeat((byte)0x61, 32).ToArray(), [0x12, 0x34, 0x56],
                                       [0x01, 127, 0, 0, 1, 0x26, 0x07], Raw(300, 0x71), receivedAt);
        var extremeNode = new GraphNodeRecord(Key(0x32), uint.MaxValue, [], new byte[32], [0, 0, 0], [],
                                              Raw(64, 0x72), DateTimeOffset.MaxValue.ToOffset(TimeSpan.Zero));
        await SaveAsync(contextFactory, async c =>
        {
            var repository = new GraphDbRepository(c);
            await repository.UpsertNodeAsync(node);
            await repository.UpsertNodeAsync(extremeNode);

            // Staged rows are visible to single-key reads of the same unit of work
            AssertNode(node, await repository.GetNodeAsync(node.NodeId));
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            AssertNode(node, await repository.GetNodeAsync(node.NodeId));
            AssertNode(extremeNode, await repository.GetNodeAsync(extremeNode.NodeId));
            Assert.Null(await repository.GetNodeAsync(Key(0x33)));
            Assert.Equal(2, (await repository.GetNodesAsync(cancellationToken)).Count);
        }

        var newerNode = new GraphNodeRecord(node.NodeId, node.Timestamp + 1, [0x02], node.Alias,
                                            [0xFF, 0x00, 0x7F], [], Raw(310, 0x73), receivedAt.AddMinutes(1));
        await SaveAsync(contextFactory, c => new GraphDbRepository(c).UpsertNodeAsync(newerNode), cancellationToken);
        await using (var context = contextFactory())
            AssertNode(newerNode, await new GraphDbRepository(context).GetNodeAsync(node.NodeId));

        await SaveAsync(contextFactory, async c =>
        {
            Assert.True(await new GraphDbRepository(c).DeleteNodeAsync(extremeNode.NodeId));
            Assert.False(await new GraphDbRepository(c).DeleteNodeAsync(Key(0x33)));
        }, cancellationToken);
        await using (var context = contextFactory())
            Assert.Single(await new GraphDbRepository(context).GetNodesAsync(cancellationToken));

        // Channels with their policies
        var channelA = new GraphChannelRecord(scidA, Key(0x31), Key(0x32), Key(0x41), Key(0x42), 16_777_215,
                                              [0x01, 0x00], Raw(430, 0x74), GraphChannelVerification.Verified, null,
                                              receivedAt);
        var channelB = new GraphChannelRecord(scidB, Key(0x33), Key(0x34), Key(0x43), Key(0x44), long.MaxValue,
                                              [], Raw(432, 0x75), GraphChannelVerification.Own, 700,
                                              receivedAt);
        var policyA0 = new GraphPolicyRecord(scidA, 0, 1_758_000_001, 0x01, 0x00, 40, 1_000, 990_000_000, 1_000, 100,
                                             Raw(136, 0x76));
        var policyA1 = new GraphPolicyRecord(scidA, 1, uint.MaxValue, 0x03, 0x03, ushort.MaxValue, ulong.MaxValue,
                                             ulong.MaxValue, uint.MaxValue, uint.MaxValue, Raw(140, 0x77));
        var policyB0 = new GraphPolicyRecord(scidB, 0, 1, 0x01, 0x02, 144, 0, 1, 0, 0, Raw(136, 0x78));
        await SaveAsync(contextFactory, async c =>
        {
            var repository = new GraphDbRepository(c);
            await repository.UpsertChannelAsync(channelA);
            await repository.UpsertChannelAsync(channelB);
            await repository.UpsertPolicyAsync(policyA0);
            await repository.UpsertPolicyAsync(policyA1);
            await repository.UpsertPolicyAsync(policyB0);
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            AssertChannel(channelA, await repository.GetChannelAsync(scidA));
            AssertChannel(channelB, await repository.GetChannelAsync(scidB));
            Assert.Null(await repository.GetChannelAsync(new ShortChannelId(1, 2, 3)));
            Assert.Equal(2, (await repository.GetChannelsAsync(cancellationToken)).Count);

            var policies = await repository.GetPoliciesAsync(scidA);
            Assert.Equal(2, policies.Count);
            AssertPolicy(policyA0, policies[0]);
            AssertPolicy(policyA1, policies[1]);
            Assert.Equal(3, (await repository.GetAllPoliciesAsync(cancellationToken)).Count);
        }

        // A newer update replaces its direction only
        var newerPolicyA0 = policyA0 with
        {
            Timestamp = policyA0.Timestamp + 60,
            FeeBaseMsat = 2_000,
            RawUpdate = Raw(138, 0x79)
        };
        await SaveAsync(contextFactory, c => new GraphDbRepository(c).UpsertPolicyAsync(newerPolicyA0),
                        cancellationToken);
        await using (var context = contextFactory())
        {
            var policies = await new GraphDbRepository(context).GetPoliciesAsync(scidA);
            AssertPolicy(newerPolicyA0, policies[0]);
            AssertPolicy(policyA1, policies[1]);
        }

        // A policy needs its channel (foreign key)
        await using (var context = contextFactory())
        {
            await new GraphDbRepository(context).UpsertPolicyAsync(
                policyB0 with { ShortChannelId = new ShortChannelId(5, 5, 5) });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }

        // Spends: A spent at 800, B already at 700; a reorg to 750 clears A only; the pruner finds B at 750
        await SaveAsync(contextFactory, async c =>
        {
            Assert.True(await new GraphDbRepository(c).MarkChannelSpentAsync(scidA, 800));
            Assert.False(await new GraphDbRepository(c).MarkChannelSpentAsync(new ShortChannelId(9, 9, 9), 800));
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            Assert.Equal(800U, (await repository.GetChannelAsync(scidA))!.SpentAtHeight);
            Assert.Equal([scidA, scidB],
                         (await repository.GetChannelsSpentAtOrBelowAsync(800)).OrderBy(s => s.BlockHeight));
            Assert.Equal([scidB], await repository.GetChannelsSpentAtOrBelowAsync(799));
        }

        await SaveAsync(contextFactory,
                        async c => Assert.Equal(1, await new GraphDbRepository(c).ClearSpentAboveAsync(750)),
                        cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            Assert.Null((await repository.GetChannelAsync(scidA))!.SpentAtHeight);
            Assert.Equal(700U, (await repository.GetChannelAsync(scidB))!.SpentAtHeight);
            Assert.Equal([scidB], await repository.GetChannelsSpentAtOrBelowAsync(750));
            Assert.Empty(await repository.GetChannelsSpentAtOrBelowAsync(699));
        }

        // Deleting a channel deletes its policies, and only its own
        await SaveAsync(contextFactory, async c =>
        {
            Assert.True(await new GraphDbRepository(c).DeleteChannelAsync(scidB));
            Assert.False(await new GraphDbRepository(c).DeleteChannelAsync(scidB));
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            Assert.Null(await repository.GetChannelAsync(scidB));
            Assert.Empty(await repository.GetPoliciesAsync(scidB));
            Assert.Equal(2, (await repository.GetAllPoliciesAsync(cancellationToken)).Count);
        }

        // A channel replaced by a newer announcement keeps its policies
        var reverifiedA = channelA with
        {
            Verification = GraphChannelVerification.Unverified,
            RawAnnouncement = Raw(431, 0x7A)
        };
        await SaveAsync(contextFactory, c => new GraphDbRepository(c).UpsertChannelAsync(reverifiedA),
                        cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            AssertChannel(reverifiedA, await repository.GetChannelAsync(scidA));
            Assert.Equal(2, (await repository.GetPoliciesAsync(scidA)).Count);
        }

        // Bans: active until their end, replaced, expired ones deleted
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var activeBan = new GraphBannedNodeRecord(Key(0x51), "5 invalid signatures in 10 min", now.AddHours(1));
        var expiredBan = new GraphBannedNodeRecord(Key(0x52), "chain mismatch", now.AddTicks(-1));
        await SaveAsync(contextFactory, async c =>
        {
            await new GraphDbRepository(c).UpsertBanAsync(activeBan);
            await new GraphDbRepository(c).UpsertBanAsync(expiredBan);
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            AssertBan(activeBan, await repository.GetBanAsync(activeBan.NodeId));
            AssertBan(expiredBan, await repository.GetBanAsync(expiredBan.NodeId));
            AssertBan(activeBan, Assert.Single(await repository.GetActiveBansAsync(now)));
        }

        var extendedBan = activeBan with { Reason = "banned again", Until = now.AddDays(1) };
        await SaveAsync(contextFactory, async c =>
        {
            await new GraphDbRepository(c).UpsertBanAsync(extendedBan);
            Assert.Equal(1, await new GraphDbRepository(c).DeleteExpiredBansAsync(now));
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new GraphDbRepository(context);
            AssertBan(extendedBan, await repository.GetBanAsync(activeBan.NodeId));
            Assert.Null(await repository.GetBanAsync(expiredBan.NodeId));
            Assert.Empty(await repository.GetActiveBansAsync(now.AddDays(1)));
        }
    }

    private static async Task SeedConfigAndKeySetsAsync(NLightningDbContext context, DatabaseType databaseType,
                                                         ChannelId channelId, CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("ChannelConfigs",
                       ("ChannelId", "{0}"), ("MinimumDepth", "3"), ("LocalToSelfDelay", "144"),
                       ("RemoteToSelfDelay", "720"), ("LocalMaxAcceptedHtlcs", "483"),
                       ("RemoteMaxAcceptedHtlcs", "30"), ("LocalDustLimitAmountSats", "546"),
                       ("RemoteDustLimitAmountSats", "354"), ("LocalHtlcMinimumMsat", "1000"),
                       ("RemoteHtlcMinimumMsat", "1"), ("LocalChannelReserveAmountSats", "10000"),
                       ("RemoteChannelReserveAmountSats", "10000"), ("LocalMaxHtlcValueInFlightMsat", "990000000"),
                       ("RemoteMaxHtlcValueInFlightMsat", "990000000"), ("FeeRatePerKwSatoshis", "2500"),
                       ("OptionAnchorOutputs", sql.Bool(false)), ("UseScidAlias", "0"),
                       ("HasInferredParams", sql.Bool(false))),
            [(byte[])channelId], cancellationToken);

        foreach (var (isLocal, seed, keyIndex) in new[] { (true, (byte)0x11, 7), (false, (byte)0x21, 0) })
        {
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("ChannelKeySets",
                           ("ChannelId", "{0}"), ("IsLocal", sql.Bool(isLocal)), ("FundingPubKey", "{1}"),
                           ("RevocationBasepoint", "{2}"), ("PaymentBasepoint", "{3}"),
                           ("DelayedPaymentBasepoint", "{4}"), ("HtlcBasepoint", "{5}"),
                           ("CurrentPerCommitmentIndex", "281474976710655"), ("CurrentPerCommitmentPoint", "{6}"),
                           ("KeyIndex", $"{keyIndex}")),
                [
                    (byte[])channelId, (byte[])Key(seed), (byte[])Key((byte)(seed + 1)),
                    (byte[])Key((byte)(seed + 2)), (byte[])Key((byte)(seed + 3)), (byte[])Key((byte)(seed + 4)),
                    (byte[])Key((byte)(seed + 5))
                ], cancellationToken);
        }
    }

    private static async Task SeedLocalCommitmentBroadcastAsync(NLightningDbContext context,
                                                                DatabaseType databaseType, ChannelId channelId,
                                                                CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        foreach (var (seed, number) in new[] { ((byte)0x63, 6), ((byte)0x64, 4) })
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("BroadcastTransactions",
                           ("TransactionId", "{0}"), ("ChannelId", "{1}"), ("RawTransaction", "{2}"),
                           ("Purpose", $"{(byte)BroadcastPurpose.LocalCommitment}"), ("FeeratePerKw", "2500"),
                           ("FirstBroadcastHeight", "900"), ("State", "0"), ("CreatedAt", "638940000000000000"),
                           ("CommitmentNumber", $"{number}")),
                [(byte[])ChainWatchSchemaRoundTrip.TxIdOf(seed), (byte[])channelId, new byte[] { 0x02, 0x00 }],
                cancellationToken);
    }

    private static CompactPubKey Key(byte seed)
    {
        var bytes = Enumerable.Repeat(seed, 33).ToArray();
        bytes[0] = 0x02;
        return bytes;
    }

    private static byte[] Raw(int length, byte seed) =>
        Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    private static async Task SaveAsync(Func<NLightningDbContext> contextFactory,
                                        Func<NLightningDbContext, Task> stage, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        await stage(context);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static void AssertNode(GraphNodeRecord expected, GraphNodeRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.Features, actual.Features);
        Assert.Equal(expected.Alias, actual.Alias);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Addresses, actual.Addresses);
        Assert.Equal(expected.RawAnnouncement, actual.RawAnnouncement);
        Assert.Equal(expected.ReceivedAt.UtcTicks, actual.ReceivedAt.UtcTicks);
    }

    private static void AssertChannel(GraphChannelRecord expected, GraphChannelRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ShortChannelId, actual.ShortChannelId);
        Assert.Equal(expected.NodeId1, actual.NodeId1);
        Assert.Equal(expected.NodeId2, actual.NodeId2);
        Assert.Equal(expected.BitcoinKey1, actual.BitcoinKey1);
        Assert.Equal(expected.BitcoinKey2, actual.BitcoinKey2);
        Assert.Equal(expected.CapacitySat, actual.CapacitySat);
        Assert.Equal(expected.Features, actual.Features);
        Assert.Equal(expected.RawAnnouncement, actual.RawAnnouncement);
        Assert.Equal(expected.Verification, actual.Verification);
        Assert.Equal(expected.SpentAtHeight, actual.SpentAtHeight);
        Assert.Equal(expected.ReceivedAt.UtcTicks, actual.ReceivedAt.UtcTicks);
    }

    private static void AssertPolicy(GraphPolicyRecord expected, GraphPolicyRecord actual)
    {
        Assert.Equal(expected.ShortChannelId, actual.ShortChannelId);
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.MessageFlags, actual.MessageFlags);
        Assert.Equal(expected.ChannelFlags, actual.ChannelFlags);
        Assert.Equal(expected.CltvExpiryDelta, actual.CltvExpiryDelta);
        Assert.Equal(expected.HtlcMinimumMsat, actual.HtlcMinimumMsat);
        Assert.Equal(expected.HtlcMaximumMsat, actual.HtlcMaximumMsat);
        Assert.Equal(expected.FeeBaseMsat, actual.FeeBaseMsat);
        Assert.Equal(expected.FeeProportionalMillionths, actual.FeeProportionalMillionths);
        Assert.Equal(expected.RawUpdate, actual.RawUpdate);
    }

    private static void AssertBan(GraphBannedNodeRecord expected, GraphBannedNodeRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Until.UtcTicks, actual.Until.UtcTicks);
    }
}