using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Onchain.Enums;
using Domain.Onchain.Events;
using Domain.Onchain.Models;
using static ChainWatchSchemaRoundTrip;

/// <summary>
/// BOLT 5 plan O0 on the real SQLite schema: the chain monitor's broadcast set (O0-T1, NL-258), outpoint watching
/// (O0-T2), block atomicity, tip processing, halting and reorgs (O0-T3, NL-214, NL-215, NL-216, NL-096).
/// </summary>
public class ChainMonitorPersistenceTests
{
    [Fact]
    public async Task Given_SendFails_When_NextBlockArrives_Then_ItIsRebroadcastUntilABlockHoldsIt()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var transaction = CreateTransaction(0x01);
        var broadcast = new BroadcastTransactionModel(ToSigned(transaction), BroadcastPurpose.Sweep,
                                                      ChannelIdOf(0x01), harness.Monitor.LastProcessedBlockHeight);
        harness.Chain.SendFailure = new InvalidOperationException("node down");

        // Act
        var accepted = await harness.Monitor.SaveAndPublishAsync(broadcast);

        // Assert: refused, but stored as pending
        Assert.False(accepted);
        Assert.Equal(BroadcastState.Pending, (await LoadBroadcastAsync(harness, transaction)).State);
        Assert.Empty(harness.Chain.Mempool);

        // Act: the node is back and a block arrives without it
        harness.Chain.SendFailure = null;
        await harness.MineAndDeliverAsync();

        // Assert: sent again after the block
        Assert.Contains(harness.Chain.Mempool, t => t.GetHash() == transaction.GetHash());
        Assert.Equal(2, harness.Chain.SendAttempts.Count(t => t.GetHash() == transaction.GetHash()));

        // Act: a block holds it
        var block = await harness.MineAndDeliverAsync();

        // Assert: confirmed with its block, and not sent any more
        var stored = await LoadBroadcastAsync(harness, transaction);
        Assert.Equal(BroadcastState.Confirmed, stored.State);
        Assert.Equal(harness.Chain.TipHeight, stored.ConfirmedHeight);
        Assert.Equal(block.GetHash().ToBytes(), (byte[])stored.ConfirmedBlockHash!.Value);
        var attempts = harness.Chain.SendAttempts.Count;
        await harness.MineAndDeliverAsync();
        Assert.Equal(attempts, harness.Chain.SendAttempts.Count);
    }

    [Fact]
    public async Task Given_FundingTransactionSavedButNotPublished_When_Restarting_Then_ItIsPublishedAtStartup()
    {
        // Arrange (NL-258: the funder saved V1FundingSigned with its funding transaction, then crashed before the send)
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var funding = CreateTransaction(0x02);
        await using (var context = harness.Context())
        {
            new Infrastructure.Repositories.Database.Onchain.BroadcastTransactionDbRepository(context)
               .Add(new BroadcastTransactionModel(ToSigned(funding), BroadcastPurpose.Funding, ChannelIdOf(0x02),
                                                  100));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(harness.Chain.SendAttempts);

        // Act
        await harness.RestartAsync();

        // Assert
        Assert.Contains(harness.Chain.Mempool, t => t.GetHash() == funding.GetHash());

        // Act: it confirms; a later restart does not send it again
        await harness.MineAndDeliverAsync();
        var attempts = harness.Chain.SendAttempts.Count;
        await harness.RestartAsync();

        // Assert
        Assert.Equal(BroadcastState.Confirmed, (await LoadBroadcastAsync(harness, funding)).State);
        Assert.Equal(attempts, harness.Chain.SendAttempts.Count);
    }

    [Fact]
    public async Task Given_BlockSpendsWatchedOutpoint_When_Processed_Then_EventRaisedOnceWithSpenderAndRowMarked()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var funding = CreateTransaction(0x03);
        var channelId = ChannelIdOf(0x03);
        await harness.Monitor.WatchOutpointAsync(new WatchedOutpointModel(new TxId(funding.GetHash().ToBytes()), 1,
                                                                          channelId,
                                                                          WatchedOutpointPurpose.FundingOutput));
        var spend = CreateSpend(funding, 1);
        var unrelated = CreateSpend(funding, 0);
        var raised = new List<OutpointSpentEventArgs>();
        harness.Monitor.OnWatchedOutpointSpent += (_, args) => raised.Add(args);

        // Act
        var block = await harness.MineAndDeliverAsync(unrelated, spend);

        // Assert
        var args = Assert.Single(raised);
        Assert.Equal(channelId, args.ChannelId);
        Assert.Equal(new TxId(spend.GetHash().ToBytes()), args.SpendingTransaction.TxId);
        Assert.Equal(spend.ToBytes(), args.SpendingTransaction.RawTxBytes);
        Assert.Equal(101u, args.BlockHeight);
        Assert.Equal(2u, args.TransactionIndex);
        Assert.Equal(new TxId(funding.GetHash().ToBytes()), args.SpentTransactionId);
        Assert.Equal(1u, args.SpentOutputIndex);
        Assert.Equal(block.GetHash().ToBytes(), (byte[])args.BlockHash!.Value);

        await using (var context = harness.Context())
        {
            var row = await context.WatchedOutpoints.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new TxId(spend.GetHash().ToBytes()), row.SpentByTransactionId);
            Assert.Equal(101u, row.SpentAtHeight);
        }

        // Act: a restart reloads the watch and replays the last processed block
        var afterRestart = new List<OutpointSpentEventArgs>();
        await harness.RestartAsync(m => m.OnWatchedOutpointSpent += (_, args2) => afterRestart.Add(args2));

        // Assert: raised again for the replayed block (the consumer contract is idempotent)
        var replayed = Assert.Single(afterRestart);
        Assert.Equal(args.SpendingTransaction.TxId, replayed.SpendingTransaction.TxId);
        Assert.Equal(101u, replayed.BlockHeight);
    }

    [Fact]
    public async Task Given_FundingTransactionOfAStoredChannelConfirms_When_Processed_Then_ItsFundingOutputIsWatched()
    {
        // Arrange (the fundee path: only the funding txid is watched, the channel row holds the output index)
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var funding = CreateTransaction(0x04);
        await harness.SeedChannelAsync(0x04, funding, 1, ChannelState.V1FundingSigned);
        await harness.Monitor.WatchTransactionAsync(ChannelIdOf(0x04), new TxId(funding.GetHash().ToBytes()), 3);
        var raised = new List<OutpointSpentEventArgs>();
        harness.Monitor.OnWatchedOutpointSpent += (_, args) => raised.Add(args);

        // Act: the funding transaction and a spend of its funding output in the same block
        await harness.MineAndDeliverAsync(funding, CreateSpend(funding, 1));

        // Assert
        await using (var context = harness.Context())
        {
            var row = await context.WatchedOutpoints.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new TxId(funding.GetHash().ToBytes()), row.TransactionId);
            Assert.Equal(1u, row.OutputIndex);
            Assert.Equal(ChannelIdOf(0x04), row.ChannelId);
            Assert.Equal((byte)WatchedOutpointPurpose.FundingOutput, row.Purpose);
            Assert.Equal(101u, row.SpentAtHeight);
        }

        Assert.Equal(ChannelIdOf(0x04), Assert.Single(raised).ChannelId);
    }

    [Fact]
    public async Task Given_ChannelsWithoutAWatch_When_Starting_Then_FundingOutputsOfOpenChannelsAreWatched()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        var open = CreateTransaction(0x05);
        var closed = CreateTransaction(0x06);
        await harness.SeedChannelAsync(0x05, open, 0, ChannelState.Open);
        await harness.SeedChannelAsync(0x06, closed, 0, ChannelState.Closed);
        var raised = new List<OutpointSpentEventArgs>();
        harness.Monitor.OnWatchedOutpointSpent += (_, args) => raised.Add(args);

        // Act
        await harness.StartAsync(95);
        await harness.MineAndDeliverAsync(CreateSpend(open, 0), CreateSpend(closed, 0));

        // Assert
        await using (var context = harness.Context())
        {
            var row = await context.WatchedOutpoints.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChannelIdOf(0x05), row.ChannelId);
        }

        Assert.Equal(ChannelIdOf(0x05), Assert.Single(raised).ChannelId);
    }

    [Fact]
    public async Task Given_BlockFailsMidway_When_Processed_Then_NothingPersistedAndLaterRoundProcessesItOnce()
    {
        // Arrange (NL-214)
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var watchedTx = CreateTransaction(0x07);
        var funding = CreateTransaction(0x08);
        await harness.SeedChannelAsync(0x07, watchedTx, 0, ChannelState.V1FundingSigned);
        await harness.Monitor.WatchTransactionAsync(ChannelIdOf(0x07), new TxId(watchedTx.GetHash().ToBytes()), 1);
        await harness.Monitor.WatchOutpointAsync(new WatchedOutpointModel(new TxId(funding.GetHash().ToBytes()), 0,
                                                                          ChannelIdOf(0x08),
                                                                          WatchedOutpointPurpose.FundingOutput));
        var broadcast = CreateTransaction(0x09);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(ToSigned(broadcast),
                                                                                BroadcastPurpose.Sweep, null, 100));
        var events = new List<string>();
        harness.Monitor.OnNewBlockDetected += (_, args) => events.Add($"block {args.Height}");
        harness.Monitor.OnTransactionConfirmed += (_, _) => events.Add("confirmed");
        harness.Monitor.OnWatchedOutpointSpent += (_, _) => events.Add("spent");
        harness.FailSaves = true;

        // Act: block 101 holds the watched tx, a spend of the watched outpoint and our broadcast; its save fails
        await harness.MineAndDeliverAsync(watchedTx, CreateSpend(funding, 0));

        // Assert: nothing of block 101 is stored, nothing was raised, processing is halted
        Assert.True(harness.Monitor.IsChainProcessingHalted);
        Assert.Equal(100u, harness.Monitor.LastProcessedBlockHeight);
        Assert.Empty(events);
        await using (var context = harness.Context())
        {
            Assert.Equal(100u, (await context.BlockchainStates.AsNoTracking()
                                             .SingleAsync(TestContext.Current.CancellationToken)).LastProcessedHeight);
            Assert.DoesNotContain(await context.BlockHeaders.AsNoTracking()
                                               .ToListAsync(TestContext.Current.CancellationToken), h => h.Height > 100);
            Assert.Null((await context.WatchedTransactions.AsNoTracking()
                                      .SingleAsync(TestContext.Current.CancellationToken)).FirstSeenAtHeight);
            Assert.Null((await context.WatchedOutpoints.AsNoTracking()
                                      .SingleAsync(o => o.ChannelId == ChannelIdOf(0x08),
                                                   TestContext.Current.CancellationToken)).SpentAtHeight);
            Assert.Equal((byte)BroadcastState.Pending,
                         (await context.BroadcastTransactions.AsNoTracking()
                                       .SingleAsync(TestContext.Current.CancellationToken)).State);
        }

        // Act: the failure clears and block 102 arrives
        harness.FailSaves = false;
        await harness.MineAndDeliverAsync();

        // Assert: 101 then 102, each effect once
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        Assert.Equal(102u, harness.Monitor.LastProcessedBlockHeight);
        Assert.Equal(["block 101", "confirmed", "spent", "block 102"], events);
        await using (var context = harness.Context())
        {
            var watch = await context.WatchedTransactions.AsNoTracking()
                                     .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(101u, watch.FirstSeenAtHeight);
            Assert.Equal(1u, watch.TransactionIndex);
            Assert.NotNull(watch.CompletedAt);
            Assert.Equal(101u, (await context.WatchedOutpoints.AsNoTracking()
                                             .SingleAsync(o => o.ChannelId == ChannelIdOf(0x08),
                                                          TestContext.Current.CancellationToken)).SpentAtHeight);
            var stored = await context.BroadcastTransactions.AsNoTracking()
                                      .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal((byte)BroadcastState.Confirmed, stored.State);
            Assert.Equal(101u, stored.ConfirmedHeight);
        }
    }

    [Fact]
    public async Task Given_ReorgOfDepth2_When_NewBranchArrives_Then_HeightsRolledBackAndNewBranchProcessed()
    {
        // Arrange (NL-096): block 101 holds a watched tx, a spend of a watched outpoint and our broadcast
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var watchedTx = CreateTransaction(0x0a);
        var funding = CreateTransaction(0x0b);
        var broadcast = CreateTransaction(0x0c);
        await harness.SeedChannelAsync(0x0a, watchedTx, 0, ChannelState.V1FundingSigned);
        var watchedTxId = new TxId(watchedTx.GetHash().ToBytes());
        await harness.Monitor.WatchTransactionAsync(ChannelIdOf(0x0a), watchedTxId, 3);
        await harness.Monitor.WatchOutpointAsync(new WatchedOutpointModel(new TxId(funding.GetHash().ToBytes()), 0,
                                                                          ChannelIdOf(0x0b),
                                                                          WatchedOutpointPurpose.FundingOutput));
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(ToSigned(broadcast),
                                                                                BroadcastPurpose.Sweep, null, 100));
        await harness.MineAndDeliverAsync(watchedTx, CreateSpend(funding, 0));
        await harness.MineAndDeliverAsync();
        Assert.Equal(102u, harness.Monitor.LastProcessedBlockHeight);

        var disconnected = new List<BlockDisconnectedEventArgs>();
        var confirmed = new List<uint>();
        harness.Monitor.OnBlockDisconnected += (_, args) => disconnected.Add(args);
        harness.Monitor.OnTransactionConfirmed += (_, args) => confirmed.Add(args.Height);

        // Act: a branch from 100 with the watched tx alone in its first block becomes the active chain
        harness.Chain.Reorg(100, 3, watchedTx);
        await harness.DeliverTipAsync();

        // Assert: 102 and 101 were disconnected, highest first
        Assert.Equal([102u, 101u], disconnected.Select(d => d.Height));
        Assert.All(disconnected, d => Assert.Equal(100u, d.ForkHeight));
        Assert.Equal(103u, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);

        // Assert: the rows follow the new branch
        await using (var context = harness.Context())
        {
            var state = await context.BlockchainStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(103u, state.LastProcessedHeight);
            Assert.Equal(harness.Chain[103].GetHash().ToBytes(), (byte[])state.LastProcessedBlockHash);

            var headers = await context.BlockHeaders.AsNoTracking().Where(h => h.Height > 100)
                                       .OrderBy(h => h.Height).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([101u, 102u, 103u], headers.Select(h => h.Height));
            for (var height = 101u; height <= 103; height++)
                Assert.Equal(harness.Chain[height].GetHash().ToBytes(),
                             (byte[])headers[(int)height - 101].BlockHash);

            // The watched tx was found again in the new 101 and reached its depth of 3 at 103
            var watch = await context.WatchedTransactions.AsNoTracking()
                                     .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(101u, watch.FirstSeenAtHeight);
            Assert.Equal(1u, watch.TransactionIndex);
            Assert.NotNull(watch.CompletedAt);

            // The spend is not in the new branch
            Assert.Null((await context.WatchedOutpoints.AsNoTracking()
                                      .SingleAsync(o => o.ChannelId == ChannelIdOf(0x0b),
                                                   TestContext.Current.CancellationToken)).SpentAtHeight);

            // Our broadcast is not either: pending again
            Assert.Equal((byte)BroadcastState.Pending,
                         (await context.BroadcastTransactions.AsNoTracking()
                                       .SingleAsync(TestContext.Current.CancellationToken)).State);
        }

        Assert.Equal([103u], confirmed);
        Assert.Contains(harness.Chain.Mempool, t => t.GetHash() == broadcast.GetHash());
    }

    [Fact]
    public async Task Given_ShorterCompetingBranch_When_ItsTipArrives_Then_RewoundToItWithoutHalting()
    {
        // Arrange (invalidateblock plus one new block: the active chain is shorter than what we processed)
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        await harness.MineAndDeliverAsync();
        await harness.MineAndDeliverAsync();
        await harness.MineAndDeliverAsync();
        var disconnected = new List<uint>();
        harness.Monitor.OnBlockDisconnected += (_, args) => disconnected.Add(args.Height);

        // Act
        harness.Chain.Reorg(100, 1);
        await harness.DeliverTipAsync();

        // Assert
        Assert.Equal([103u, 102u, 101u], disconnected);
        Assert.Equal(101u, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        await using (var context = harness.Context())
        {
            var state = await context.BlockchainStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(harness.Chain[101].GetHash().ToBytes(), (byte[])state.LastProcessedBlockHash);
        }
    }

    [Fact]
    public async Task Given_NewBranchProcessed_When_AStaleOrphanIsDeliveredLate_Then_ItIsDroppedWithoutARewind()
    {
        // Arrange: 101 and 102 processed, then a branch from 100 (101', 102', 103') replaces them
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        await harness.MineAndDeliverAsync();
        var orphan = await harness.MineAndDeliverAsync();
        harness.Chain.Reorg(100, 3);
        await harness.DeliverTipAsync();
        Assert.Equal(103u, harness.Monitor.LastProcessedBlockHeight);
        var disconnected = new List<uint>();
        harness.Monitor.OnBlockDisconnected += (_, args) => disconnected.Add(args.Height);
        var processed = new List<uint>();
        harness.Monitor.OnNewBlockDetected += (_, args) => processed.Add(args.Height);

        // Act: a late notification of the old 102, which is not in the active chain
        await harness.Monitor.ProcessNewBlockAsync(orphan, 102);

        // Assert: nothing of the active branch was disconnected or processed again
        Assert.Empty(disconnected);
        Assert.Empty(processed);
        Assert.Equal(103u, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        await using (var context = harness.Context())
        {
            var state = await context.BlockchainStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(harness.Chain[103].GetHash().ToBytes(), (byte[])state.LastProcessedBlockHash);
            var headers = await context.BlockHeaders.AsNoTracking().Where(h => h.Height > 100)
                                       .OrderBy(h => h.Height).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([101u, 102u, 103u], headers.Select(h => h.Height));
        }

        // Act: the chain grows on the active branch
        await harness.MineAndDeliverAsync();

        // Assert
        Assert.Equal([104u], processed);
        Assert.Empty(disconnected);
    }

    [Fact]
    public async Task Given_PendingBroadcast_When_ABlockHaltsProcessing_Then_ItIsStillSentAfterTheRound()
    {
        // Arrange: a refused broadcast (stored as pending), then every block save fails
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        harness.Monitor.BlockRetryBaseDelay = TimeSpan.Zero;
        var commitment = CreateTransaction(0x0d);
        harness.Chain.SendFailure = new InvalidOperationException("node down");
        Assert.False(await harness.Monitor.SaveAndPublishAsync(
                         new BroadcastTransactionModel(ToSigned(commitment), BroadcastPurpose.LocalCommitment,
                                                       ChannelIdOf(0x0d), 100)));
        harness.Chain.SendFailure = null;
        harness.FailSaves = true;

        // Act
        await harness.MineAndDeliverAsync();

        // Assert: the round halted, and the pending transaction was sent anyway
        Assert.True(harness.Monitor.IsChainProcessingHalted);
        Assert.Contains(harness.Chain.Mempool, t => t.GetHash() == commitment.GetHash());
        Assert.Equal(2, harness.Chain.SendAttempts.Count(t => t.GetHash() == commitment.GetHash()));
    }

    [Fact]
    public async Task Given_PendingBroadcast_When_StartupHaltsOnADeepReorg_Then_ItIsStillSentAtStartup()
    {
        // Arrange: a refused broadcast, then the chain loses every block we keep a hash of while we are down
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var funding = CreateTransaction(0x0e);
        harness.Chain.SendFailure = new InvalidOperationException("node down");
        Assert.False(await harness.Monitor.SaveAndPublishAsync(
                         new BroadcastTransactionModel(ToSigned(funding), BroadcastPurpose.Funding, ChannelIdOf(0x0e),
                                                       100)));
        await harness.Monitor.StopAsync();
        harness.Chain.SendFailure = null;
        harness.Chain.Reorg(90, 0);

        // Act
        await harness.RestartAsync(alreadyStopped: true);

        // Assert
        Assert.True(harness.Monitor.IsChainProcessingHalted);
        Assert.Contains(harness.Chain.Mempool, t => t.GetHash() == funding.GetHash());
    }

    [Fact]
    public async Task Given_TheSameBlocksDeliveredAgain_When_Processed_Then_NothingIsRewound()
    {
        // Arrange (reconsiderblock reconnects blocks we already processed)
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        await harness.MineAndDeliverAsync();
        await harness.MineAndDeliverAsync();
        var disconnected = 0;
        harness.Monitor.OnBlockDisconnected += (_, _) => disconnected++;

        // Act
        await harness.Monitor.ProcessNewBlockAsync(harness.Chain[101], 101);
        await harness.Monitor.ProcessNewBlockAsync(harness.Chain[102], 102);

        // Assert
        Assert.Equal(0, disconnected);
        Assert.Equal(102u, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }

    [Fact]
    public async Task Given_TipAboveTheState_When_Starting_Then_TheTipIsProcessed()
    {
        // Arrange (NL-215)
        await using var harness = new ChainMonitorHarness(105);
        var heights = new List<uint>();
        harness.Monitor.OnNewBlockDetected += (_, args) => heights.Add(args.Height);

        // Act
        await harness.StartAsync(100);

        // Assert: 100 (the state's own block) up to and including the tip
        Assert.Equal([100u, 101u, 102u, 103u, 104u, 105u], heights);
        Assert.Equal(105u, harness.Monitor.LastProcessedBlockHeight);
    }

    [Fact]
    public async Task Given_ChainShorterThanTheStateAtStartup_When_Starting_Then_ItRewindsToTheForkPoint()
    {
        // Arrange (invalidateblock while the node was down)
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        await harness.Monitor.StopAsync();
        harness.Chain.Reorg(98, 0);
        var disconnected = new List<uint>();

        // Act
        await harness.RestartAsync(m => m.OnBlockDisconnected += (_, args) => disconnected.Add(args.Height),
                                   alreadyStopped: true);

        // Assert
        Assert.Equal([100u, 99u], disconnected);
        Assert.Equal(98u, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);

        // Act: the chain grows again on the new branch
        await harness.MineAndDeliverAsync();

        // Assert
        Assert.Equal(99u, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }

    [Fact]
    public async Task Given_ReorgDeeperThanTheHeaderRing_When_Detected_Then_ProcessingHalts()
    {
        // Arrange (NL-216: the halt is visible through IBlockchainMonitor)
        await using var harness = new ChainMonitorHarness();
        harness.Monitor.HeaderRingSize = 3;
        await harness.StartAsync(95);

        // Act
        harness.Chain.Reorg(96, 6);
        await harness.DeliverTipAsync();

        // Assert
        Assert.True(((Infrastructure.Bitcoin.Wallet.Interfaces.IBlockchainMonitor)harness.Monitor)
                       .IsChainProcessingHalted);
        Assert.Equal(100u, harness.Monitor.LastProcessedBlockHeight);
    }

    private static async Task<BroadcastTransactionModel> LoadBroadcastAsync(ChainMonitorHarness harness,
                                                                           Transaction transaction)
    {
        await using var context = harness.Context();
        var stored = await new Infrastructure.Repositories.Database.Onchain.BroadcastTransactionDbRepository(context)
                        .GetByTransactionIdAsync(new TxId(transaction.GetHash().ToBytes()));
        Assert.NotNull(stored);
        return stored;
    }

    /// <summary>A transaction with a unique input and two outputs.</summary>
    internal static Transaction CreateTransaction(byte seed)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), seed));
        transaction.Outputs.Add(Money.Satoshis(100_000), new Key().PubKey.WitHash.ScriptPubKey);
        transaction.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    internal static Transaction CreateSpend(Transaction spent, uint outputIndex)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(spent.GetHash(), outputIndex));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    private static SignedTransaction ToSigned(Transaction transaction) =>
        new(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
}