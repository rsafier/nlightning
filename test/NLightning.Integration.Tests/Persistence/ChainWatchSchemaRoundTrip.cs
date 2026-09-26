using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Database.Onchain;

/// <summary>
/// Provider-agnostic proof for migration <c>AddChainWatchAndBroadcasts</c> (BOLT 5 plan O0-T4), shared by the SQLite
/// test and the Docker Postgres/SQL Server tests: channels stored before the migration get their funding output
/// watched by its data step (not the Closed or Stale ones), and the three new tables round-trip through their
/// repositories, including the reorg rollbacks.
/// </summary>
internal static class ChainWatchSchemaRoundTrip
{
    private const string MigrationName = "_AddChainWatchAndBroadcasts";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddChainWatchAndBroadcasts, with channels in several states
        var channels = new (byte Seed, ChannelState State, ushort Index)[]
        {
            (0x21, ChannelState.V1FundingSigned, 0), (0x22, ChannelState.Open, 1), (0x23, ChannelState.Failed, 0),
            (0x24, ChannelState.Closed, 0), (0x25, ChannelState.Stale, 1)
        };
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            foreach (var (seed, state, index) in channels)
                await SeedChannelAsync(context, databaseType, ChannelIdOf(seed), TxIdOf(seed), index, state,
                                       cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: one funding output watch per channel that is neither Closed nor Stale
        await using (var context = contextFactory())
        {
            var rows = await context.WatchedOutpoints.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(3, rows.Count);
            foreach (var (seed, state, index) in channels)
            {
                var row = rows.SingleOrDefault(r => r.ChannelId == ChannelIdOf(seed));
                if (state is ChannelState.Closed or ChannelState.Stale)
                {
                    Assert.Null(row);
                    continue;
                }

                Assert.NotNull(row);
                Assert.Equal(TxIdOf(seed), row.TransactionId);
                Assert.Equal(index, row.OutputIndex);
                Assert.Equal((byte)WatchedOutpointPurpose.FundingOutput, row.Purpose);
                Assert.Null(row.SpentAtHeight);
                Assert.InRange(row.CreatedAt, before, DateTimeOffset.UtcNow.AddMinutes(5));
            }

            // The startup backfill finds nothing left to add
            var repository = new WatchedOutpointDbRepository(context);
            Assert.Empty(await repository.AddMissingFundingOutpointsAsync());
            Assert.Equal(3, (await repository.GetActiveAsync()).Count);
        }

        // Assert: the new tables round-trip on this provider
        await AssertTablesRoundTripAsync(contextFactory, databaseType, cancellationToken);
    }

    /// <summary>
    /// Every repository method of the three new tables (and the reorg helpers of the watched transactions), each write
    /// saved and read back from a fresh context.
    /// </summary>
    public static async Task AssertTablesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                        DatabaseType databaseType,
                                                        CancellationToken cancellationToken)
    {
        var channelId = ChannelIdOf(0x31);
        var fundingTxId = TxIdOf(0x32);
        var spender = TxIdOf(0x33);
        var blockHash = new Hash(Enumerable.Repeat((byte)0x34, 32).ToArray());
        var createdAt = new DateTimeOffset(2026, 9, 26, 1, 2, 3, TimeSpan.FromHours(-3)).AddTicks(4_567);

        // Watched outpoint: add -> spent -> spend cleared by a reorg
        var watch = new WatchedOutpointModel(fundingTxId, 3, channelId, WatchedOutpointPurpose.ResolutionOutput,
                                             createdAt);
        await SaveAsync(contextFactory, c =>
        {
            new WatchedOutpointDbRepository(c).Add(watch);
            return Task.CompletedTask;
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var loaded = await new WatchedOutpointDbRepository(context).GetAsync(fundingTxId, 3);
            AssertWatch(watch, loaded);
            Assert.Contains(await new WatchedOutpointDbRepository(context).GetActiveAsync(),
                            w => w.TransactionId == fundingTxId && w.OutputIndex == 3);
        }

        await SaveAsync(contextFactory,
                        c => new WatchedOutpointDbRepository(c).MarkSpentAsync(fundingTxId, 3, spender, 812, blockHash),
                        cancellationToken);
        watch.MarkSpent(spender, 812, blockHash);
        await using (var context = contextFactory())
            AssertWatch(watch, await new WatchedOutpointDbRepository(context).GetAsync(fundingTxId, 3));

        // Nothing to do for an outpoint that is not watched
        await SaveAsync(contextFactory,
                        c => new WatchedOutpointDbRepository(c).MarkSpentAsync(TxIdOf(0x3f), 0, spender, 812,
                                                                               blockHash), cancellationToken);

        await using (var context = contextFactory())
        {
            Assert.Equal(0, await new WatchedOutpointDbRepository(context).ClearSpendsAboveAsync(812));
            Assert.Equal(1, await new WatchedOutpointDbRepository(context).ClearSpendsAboveAsync(811));
            await context.SaveChangesAsync(cancellationToken);
        }

        watch.ClearSpend();
        await using (var context = contextFactory())
            AssertWatch(watch, await new WatchedOutpointDbRepository(context).GetAsync(fundingTxId, 3));

        // Broadcast: pending -> confirmed -> pending again after a reorg
        var raw = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        var broadcastTxId = TxIdOf(0x35);
        var broadcast = BroadcastTransactionModel.Restore(broadcastTxId, raw, BroadcastPurpose.Funding, channelId,
                                                          2_500, TxIdOf(0x36), 800, BroadcastState.Pending, null,
                                                          null, createdAt);
        await SaveAsync(contextFactory, c =>
        {
            new BroadcastTransactionDbRepository(c).Add(broadcast);
            return Task.CompletedTask;
        }, cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new BroadcastTransactionDbRepository(context);
            AssertBroadcast(broadcast, await repository.GetByTransactionIdAsync(broadcastTxId));
            AssertBroadcast(broadcast, Assert.Single(await repository.GetPendingAsync(),
                                                     b => b.TransactionId == broadcastTxId));
        }

        await SaveAsync(contextFactory,
                        c => new BroadcastTransactionDbRepository(c).MarkConfirmedAsync(broadcastTxId, 813, blockHash),
                        cancellationToken);
        broadcast.MarkConfirmed(813, blockHash);
        await using (var context = contextFactory())
        {
            var repository = new BroadcastTransactionDbRepository(context);
            AssertBroadcast(broadcast, await repository.GetByTransactionIdAsync(broadcastTxId));
            Assert.DoesNotContain(await repository.GetPendingAsync(), b => b.TransactionId == broadcastTxId);
        }

        await using (var context = contextFactory())
        {
            Assert.Equal(0, await new BroadcastTransactionDbRepository(context).UnconfirmAboveAsync(813));
            Assert.Equal(1, await new BroadcastTransactionDbRepository(context).UnconfirmAboveAsync(812));
            await context.SaveChangesAsync(cancellationToken);
        }

        broadcast.MarkUnconfirmed();
        await using (var context = contextFactory())
            AssertBroadcast(broadcast,
                            await new BroadcastTransactionDbRepository(context).GetByTransactionIdAsync(broadcastTxId));

        // Block headers: the ring keeps heights, replaces a height, and drops above and below
        await using (var context = contextFactory())
        {
            var repository = new BlockHeaderDbRepository(context);
            for (var height = 900u; height <= 905; height++)
                await repository.AddOrReplaceAsync(new BlockHeaderModel(height, HashOf((byte)height),
                                                                        HashOf((byte)(height - 1))));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new BlockHeaderDbRepository(context);
            await repository.AddOrReplaceAsync(new BlockHeaderModel(905, HashOf(0xee), HashOf(unchecked((byte)904))));
            await repository.DeleteBelowAsync(902);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var headers = await new BlockHeaderDbRepository(context).GetAllAsync();
            Assert.Equal([902u, 903u, 904u, 905u], headers.Select(h => h.Height));
            Assert.Equal(HashOf(0xee), headers[^1].BlockHash);
            Assert.Equal(HashOf(unchecked((byte)904)), headers[^1].PreviousBlockHash);
            await new BlockHeaderDbRepository(context).DeleteAboveAsync(903);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
            Assert.Equal([902u, 903u], (await new BlockHeaderDbRepository(context).GetAllAsync()).Select(h => h.Height));

        // Watched transactions: the reorg helpers reset pending first sightings only, and an update keeps CreatedAt
        var pending = new WatchedTransactionModel(channelId, TxIdOf(0x37), 6);
        var completed = new WatchedTransactionModel(channelId, TxIdOf(0x38), 1);
        await using (var context = contextFactory())
        {
            // A watched transaction belongs to a stored channel (foreign key)
            await SeedChannelAsync(context, databaseType, channelId, fundingTxId, 3, ChannelState.Open,
                                   cancellationToken);
            var repository = new WatchedTransactionDbRepository(context);
            repository.Add(pending);
            repository.Add(completed);
            await context.SaveChangesAsync(cancellationToken);
        }

        DateTime storedCreatedAt;
        await using (var context = contextFactory())
            storedCreatedAt = (await context.WatchedTransactions.AsNoTracking()
                                            .SingleAsync(w => w.TransactionId == pending.TransactionId,
                                                         cancellationToken)).CreatedAt;

        pending.SetHeightAndIndex(820, 2);
        completed.SetHeightAndIndex(821, 1);
        completed.MarkAsCompleted();
        await using (var context = contextFactory())
        {
            var repository = new WatchedTransactionDbRepository(context);
            repository.Update(pending);
            repository.Update(completed);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var row = await context.WatchedTransactions.AsNoTracking()
                                   .SingleAsync(w => w.TransactionId == pending.TransactionId, cancellationToken);
            Assert.Equal(storedCreatedAt, row.CreatedAt);
            Assert.Equal(820u, row.FirstSeenAtHeight);

            var repository = new WatchedTransactionDbRepository(context);
            var completedAbove = Assert.Single(await repository.GetCompletedFirstSeenAboveAsync(819));
            Assert.Equal(completed.TransactionId, completedAbove.TransactionId);
            Assert.Empty(await repository.GetCompletedFirstSeenAboveAsync(821));
            Assert.Equal(1, await repository.ResetPendingFirstSeenAboveAsync(819));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new WatchedTransactionDbRepository(context);
            var reset = await repository.GetByTransactionIdAsync(pending.TransactionId);
            Assert.NotNull(reset);
            Assert.Null(reset.FirstSeenAtHeight);
            Assert.Null(reset.TransactionIndex);
            var stillCompleted = await repository.GetByTransactionIdAsync(completed.TransactionId);
            Assert.NotNull(stillCompleted);
            Assert.True(stillCompleted.IsCompleted);
            Assert.Equal(821u, stillCompleted.FirstSeenAtHeight);
        }
    }

    /// <summary>A funded channel row with only the columns every schema since <c>AddCommitmentState</c> requires (the
    /// peer's commitment number is <paramref name="remoteCommitmentNumber"/>).</summary>
    internal static async Task SeedChannelAsync(NLightningDbContext context, DatabaseType databaseType,
                                                ChannelId channelId, TxId fundingTxId, ushort fundingOutputIndex,
                                                ChannelState state, CancellationToken cancellationToken,
                                                ulong remoteCommitmentNumber = 0)
    {
        var sql = new MigrationSqlDialect(databaseType);
        var remoteNodeId = new byte[33];
        remoteNodeId[0] = 0x02;
        remoteNodeId[32] = ((byte[])channelId)[0];
        var point = new byte[33];
        point[0] = 0x03;
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Channels",
                       ("ChannelId", "{0}"), ("FundingCreatedAtBlockHeight", "100"), ("FundingTxId", "{1}"),
                       ("FundingOutputIndex", $"{fundingOutputIndex}"), ("FundingAmountSatoshis", "1000000"),
                       ("IsInitiator", sql.Bool(true)), ("RemoteNodeId", "{2}"), ("LocalNextHtlcId", "0"),
                       ("RemoteNextHtlcId", "0"), ("LocalRevocationNumber", "0"), ("RemoteRevocationNumber", "0"),
                       ("LocalCommitmentNumber", "0"),
                       ("RemoteCommitmentNumber", $"{remoteCommitmentNumber}"), ("State", $"{(byte)state}"),
                       ("Version", "1"), ("LocalBalanceMsat", "600000000"), ("RemoteBalanceMsat", "400000000"),
                       ("RemoteNextPerCommitmentPoint", "{3}"), ("LastSentOrder", "0"),
                       ("DataLossDetected", sql.Bool(false))),
            [(byte[])channelId, (byte[])fundingTxId, remoteNodeId, point], cancellationToken);
    }

    internal static ChannelId ChannelIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    internal static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static Hash HashOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static void AssertWatch(WatchedOutpointModel expected, WatchedOutpointModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.OutputIndex, actual.OutputIndex);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.Purpose, actual.Purpose);
        Assert.Equal(expected.SpentByTransactionId, actual.SpentByTransactionId);
        Assert.Equal(expected.SpentAtHeight, actual.SpentAtHeight);
        Assert.Equal(expected.SpentBlockHash, actual.SpentBlockHash);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
    }

    private static void AssertBroadcast(BroadcastTransactionModel expected, BroadcastTransactionModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.RawTransaction, actual.RawTransaction);
        Assert.Equal(expected.Purpose, actual.Purpose);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.FeeratePerKw, actual.FeeratePerKw);
        Assert.Equal(expected.ReplacesTransactionId, actual.ReplacesTransactionId);
        Assert.Equal(expected.FirstBroadcastHeight, actual.FirstBroadcastHeight);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.ConfirmedHeight, actual.ConfirmedHeight);
        Assert.Equal(expected.ConfirmedBlockHash, actual.ConfirmedBlockHash);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
    }

    private static async Task SaveAsync(Func<NLightningDbContext> contextFactory,
                                        Func<NLightningDbContext, Task> stage, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        await stage(context);
        await context.SaveChangesAsync(cancellationToken);
    }
}