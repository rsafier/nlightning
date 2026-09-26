using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Events;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT 7 G2-T5: the graph pruner over a fake clock and fake blocks (spent + 72, reorg of the spend, stale channels,
/// nodes left without channels, our own channels, the startup txid lookup and the monitor events).
/// </summary>
public class GraphPrunerTests
{
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_carol = new(3);
    private static readonly ShortChannelId s_ab = new(110, 1, 0);
    private static readonly ShortChannelId s_bc = new(115, 1, 0);

    [Fact]
    public async Task Given_TheFundingOutputSpent_When_Blocks_Then_MarkedSpentAndRemovedAt72WithItsLonelyNode()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);

        // Act
        var marked = pruner.ApplyBlock(200, [SpendOf(s_ab)]);
        var at271 = pruner.ApplyBlock(271, []);
        var keptAt271 = kit.Store.TryGetChannel(s_ab, out var spent);
        pruner.ApplyBlock(272, []);

        // Assert
        Assert.Equal(1, marked);
        Assert.Equal(0, at271);
        Assert.True(keptAt271);
        Assert.Equal(200u, spent!.SpentAtHeight);
        Assert.False(kit.Store.TryGetChannel(s_ab, out _));
        Assert.False(kit.Store.TryGetNode(s_alice.PubKey, out _)); // alice had only alice-bob (B7-PR-01 MAY)
        Assert.True(kit.Store.TryGetNode(s_carol.PubKey, out _));
        Assert.True(kit.Store.TryGetChannel(s_bc, out _));
    }

    [Fact]
    public async Task Given_AnotherOutputOfTheFundingTransaction_When_Spent_Then_TheChannelStays()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);

        // Act
        var changes = pruner.ApplyBlock(200, [(GraphTestKit.TxIdFor(s_ab), 1u), (GraphTestKit.TxIdFor(s_bc), 3u)]);

        // Assert
        Assert.Equal(0, changes);
        Assert.True(kit.Store.TryGetChannel(s_ab, out var channel));
        Assert.Null(channel.SpentAtHeight);
    }

    [Fact]
    public async Task Given_TheSpendingBlockReplayed_When_AppliedAgain_Then_NothingChanges()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        pruner.ApplyBlock(200, [SpendOf(s_ab)]);

        // Act
        var changes = pruner.ApplyBlock(200, [SpendOf(s_ab)]);

        // Assert
        Assert.Equal(0, changes);
        Assert.True(kit.Store.TryGetChannel(s_ab, out var channel));
        Assert.Equal(200u, channel.SpentAtHeight);
    }

    [Fact]
    public async Task Given_TheSpendReorgedOut_When_ThePruneHeightPasses_Then_TheChannelStaysUntilSpentAgain()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        pruner.ApplyBlock(200, [SpendOf(s_ab)]);

        // Act: the block at 200 is disconnected (fork at 199); the new branch does not spend it until 205
        var cleared = pruner.ApplyDisconnect(199);
        pruner.ApplyBlock(200, []);
        var unspent = kit.Store.TryGetChannel(s_ab, out var afterReorg) && afterReorg.SpentAtHeight is null;
        pruner.ApplyBlock(205, [SpendOf(s_ab)]);
        pruner.ApplyBlock(276, []);
        var keptAt276 = kit.Store.TryGetChannel(s_ab, out var respent);
        pruner.ApplyBlock(277, []);

        // Assert
        Assert.Equal(1, cleared);
        Assert.True(unspent);
        Assert.True(keptAt276);
        Assert.Equal(205u, respent!.SpentAtHeight);
        Assert.False(kit.Store.TryGetChannel(s_ab, out _));
    }

    [Fact]
    public async Task Given_AReorgAboveTheSpend_When_Disconnected_Then_TheSpendIsKept()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        pruner.ApplyBlock(200, [SpendOf(s_ab)]);

        // Act
        var cleared = pruner.ApplyDisconnect(200);

        // Assert
        Assert.Equal(0, cleared);
        Assert.True(kit.Store.TryGetChannel(s_ab, out var channel));
        Assert.Equal(200u, channel.SpentAtHeight);
    }

    [Fact]
    public async Task Given_EveryUpdateOlderThanDeleteStaleAfter_When_ABlock_Then_ChannelsAndTheirNodesAreRemoved()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);

        // Act
        kit.Clock.Now = GraphTestKit.DefaultNow + kit.Options.DeleteStaleAfter;
        var atTheLimit = pruner.ApplyBlock(300, []);
        kit.Clock.Now += TimeSpan.FromSeconds(1);
        var pastTheLimit = pruner.ApplyBlock(301, []);

        // Assert
        Assert.Equal(0, atTheLimit);
        Assert.Equal(4, pastTheLimit); // two channels, alice and carol
        Assert.Equal(0, kit.Store.ChannelCount);
        Assert.Equal(0, kit.Store.NodeCount);
    }

    [Fact]
    public async Task Given_ANodeWithOnlyStaleChannels_When_Pruned_Then_ItGoesAndANodeWithAFreshChannelStays()
    {
        // Arrange: bob-carol gets a fresh update from carol; alice's only channel keeps its old ones
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        kit.Clock.Now = GraphTestKit.DefaultNow + kit.Options.DeleteStaleAfter + TimeSpan.FromHours(1);
        var fresh = (uint)kit.Clock.Now.ToUnixTimeSeconds();
        var direction = GraphTestKit.DirectionOf(s_carol, s_bob);
        Assert.True(kit.Store.TryApplyPolicy(s_bc, new GraphPolicy(fresh, 1, direction, 40, 1_000, 500_000_000, 1_000,
                                                                   100)));

        // Act
        pruner.ApplyBlock(300, []);

        // Assert
        Assert.False(kit.Store.TryGetChannel(s_ab, out _));
        Assert.False(kit.Store.TryGetNode(s_alice.PubKey, out _));
        Assert.True(kit.Store.TryGetChannel(s_bc, out _));
        Assert.True(kit.Store.TryGetNode(s_carol.PubKey, out _));
    }

    [Fact]
    public async Task Given_OneDirectionUpdatedRecently_When_TheOtherIsStale_Then_TheChannelIsKept()
    {
        // Arrange (B7-PR-02: only when the latest update of both directions is old)
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        kit.Clock.Now = GraphTestKit.DefaultNow + kit.Options.DeleteStaleAfter + TimeSpan.FromHours(1);
        var fresh = (uint)kit.Clock.Now.ToUnixTimeSeconds();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        Assert.True(kit.Store.TryApplyPolicy(s_ab, new GraphPolicy(fresh, 1, direction, 40, 1_000, 500_000_000, 1_000,
                                                                   100)));

        // Act
        pruner.ApplyBlock(300, []);

        // Assert
        Assert.True(kit.Store.TryGetChannel(s_ab, out _));
        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out _));
    }

    [Fact]
    public async Task Given_OurOwnStaleChannel_When_Pruned_Then_ItAndOurNodeStay()
    {
        // Arrange: we are alice
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit, ourNodeId: s_alice.PubKey);
        kit.Clock.Now = GraphTestKit.DefaultNow + kit.Options.DeleteStaleAfter + TimeSpan.FromDays(10);

        // Act
        pruner.ApplyBlock(300, []);

        // Assert
        Assert.True(kit.Store.TryGetChannel(s_ab, out _));
        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out _));
        Assert.False(kit.Store.TryGetChannel(s_bc, out _));
        Assert.False(kit.Store.TryGetNode(s_carol.PubKey, out _));
    }

    [Fact]
    public async Task Given_AnOwnAnnouncement_When_Stale_Then_ItIsNeverPruned()
    {
        // Arrange: our own 256 without any update (stored as Own through the sink)
        var kit = new GraphTestKit();
        var scid = new ShortChannelId(120, 2, 0);
        await kit.Ingress.ApplyOwnAsync(GraphTestKit.SignedChannelAnnouncement(scid, s_alice, s_carol,
                                                                              new TestGossipKey(11),
                                                                              new TestGossipKey(13)),
                                        null, TestContext.Current.CancellationToken);
        var pruner = CreatePruner(kit);
        kit.Clock.Now = GraphTestKit.DefaultNow + TimeSpan.FromDays(365);

        // Act
        pruner.ApplyBlock(300, []);

        // Assert
        Assert.True(kit.Store.TryGetChannel(scid, out var channel));
        Assert.Equal(GraphChannelVerification.Own, channel.Verification);
    }

    [Fact]
    public async Task Given_AChannelWithoutUpdates_When_ItsAnnouncementIsOld_Then_ItIsRemoved()
    {
        // Arrange: the announcement's arrival is the baseline
        var kit = new GraphTestKit();
        kit.FundingFound();
        var scid = new ShortChannelId(130, 1, 1);
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                                    GraphTestKit.SignedChannelAnnouncement(
                                                        scid, s_bob, s_carol, new TestGossipKey(12),
                                                        new TestGossipKey(13)), 0,
                                                    TestContext.Current.CancellationToken);
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        var pruner = CreatePruner(kit);

        // Act
        kit.Clock.Now = GraphTestKit.DefaultNow + kit.Options.DeleteStaleAfter - TimeSpan.FromMinutes(1);
        pruner.ApplyBlock(300, []);
        var keptBefore = kit.Store.TryGetChannel(scid, out _);
        kit.Clock.Now = GraphTestKit.DefaultNow + kit.Options.DeleteStaleAfter + TimeSpan.FromMinutes(1);
        pruner.ApplyBlock(301, []);

        // Assert
        Assert.True(keptBefore);
        Assert.False(kit.Store.TryGetChannel(scid, out _));
    }

    [Fact]
    public async Task Given_ARestart_When_TheTxidsAreLookedUp_Then_FoundOnesAreFollowedAndSpentOnesMarked()
    {
        // Arrange: the graph reloaded from rows saved without funding txids (before migration AddGraphFundingTxId)
        var before = await GraphStoreTests.CreateGraphAsync();
        await before.Store.FlushAsync(TestContext.Current.CancellationToken);
        ForgetFundingTxIds(before.Repository);
        var kit = new GraphTestKit(before.Repository);
        await kit.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, kit.Store.GetChannelsWithoutFundingTxId().Count);
        kit.FundingLookup.Setup(l => l.LookupAsync(s_ab, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, GraphTestKit.TxIdFor(s_ab),
                                                              LightningMoney.Satoshis(1_000_000), [0x00, 0x20], 90));
        kit.FundingLookup.Setup(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.Failed(FundingOutputStatus.OutputSpentOrMissing));
        var pruner = CreatePruner(kit);

        // Act
        var transient = await pruner.ResolveFundingTxIdsAsync(300, TestContext.Current.CancellationToken);
        pruner.ApplyBlock(301, [SpendOf(s_ab)]);

        // Assert
        Assert.Equal(0, transient);
        Assert.True(kit.Store.TryGetChannel(s_ab, out var ab));
        Assert.Equal(301u, ab.SpentAtHeight);
        Assert.True(kit.Store.TryGetChannel(s_bc, out var bc));
        Assert.Equal(300u, bc.SpentAtHeight);
    }

    [Fact]
    public async Task Given_TransientAndPermanentLookups_When_ResolvedTwice_Then_OnlyTheTransientOneIsAskedAgain()
    {
        // Arrange
        var before = await GraphStoreTests.CreateGraphAsync();
        await before.Store.FlushAsync(TestContext.Current.CancellationToken);
        ForgetFundingTxIds(before.Repository);
        var kit = new GraphTestKit(before.Repository);
        await kit.Store.LoadAsync(TestContext.Current.CancellationToken);
        kit.FundingLookup.Setup(l => l.LookupAsync(s_ab, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.Failed(FundingOutputStatus.ChainUnavailable));
        kit.FundingLookup.Setup(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.Failed(FundingOutputStatus.BlockUnavailable));
        var pruner = CreatePruner(kit);

        // Act
        var first = await pruner.ResolveFundingTxIdsAsync(300, TestContext.Current.CancellationToken);
        var second = await pruner.ResolveFundingTxIdsAsync(301, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, first);
        Assert.Equal(1, second);
        kit.FundingLookup.Verify(l => l.LookupAsync(s_ab, It.IsAny<CancellationToken>()), Times.Exactly(2));
        kit.FundingLookup.Verify(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(kit.Store.TryGetChannel(s_bc, out var bc));
        Assert.Null(bc.SpentAtHeight);
    }

    [Fact]
    public async Task Given_TheMonitorsEvents_When_Started_Then_SpendsAndReorgsReachTheDatabase()
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(199);
        var pruner = CreatePruner(kit, monitor: monitor);
        var ct = TestContext.Current.CancellationToken;

        // Act
        pruner.Start();
        await pruner.WhenIdleAsync(ct);
        monitor.Raise(m => m.OnBlockInputs += null,
                      new BlockInputsEventArgs(200, default, [SpendOf(s_ab)]));
        await pruner.WhenIdleAsync(ct);
        var persistedSpend = kit.Repository.Channels[s_ab].SpentAtHeight;
        monitor.Raise(m => m.OnBlockDisconnected += null, new BlockDisconnectedEventArgs(200, default, 199));
        await pruner.WhenIdleAsync(ct);
        var afterReorg = kit.Repository.Channels[s_ab].SpentAtHeight;
        monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(200, default, [SpendOf(s_bc)]));
        await pruner.WhenIdleAsync(ct);
        await pruner.StopAsync();
        monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(272, default, []));

        // Assert
        Assert.Equal(200u, persistedSpend);
        Assert.Null(afterReorg);
        Assert.Equal(200u, kit.Repository.Channels[s_bc].SpentAtHeight);
        Assert.True(kit.Store.TryGetChannel(s_bc, out _)); // stopped: block 272 was not applied
        Assert.Equal(200u, pruner.LastHeight);
    }

    [Fact]
    public async Task Given_AMonitorWithoutHeightAtStart_When_AChannelWasSpentWhileDown_Then_ItIsMarkedAtTheFirstBlockAndKept72Blocks()
    {
        // Arrange: the graph reloaded without txids; the monitor loads its height only when it starts (after us)
        var before = await GraphStoreTests.CreateGraphAsync();
        await before.Store.FlushAsync(TestContext.Current.CancellationToken);
        ForgetFundingTxIds(before.Repository);
        var kit = new GraphTestKit(before.Repository);
        kit.FundingLookup.Setup(l => l.LookupAsync(s_ab, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.Failed(FundingOutputStatus.OutputSpentOrMissing));
        kit.FundingLookup.Setup(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, GraphTestKit.TxIdFor(s_bc),
                                                              LightningMoney.Satoshis(1_000_000), [0x00, 0x20], 90));
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(0);
        var pruner = CreatePruner(kit, monitor: monitor);
        var ct = TestContext.Current.CancellationToken;

        // Act
        pruner.Start();
        await pruner.WhenIdleAsync(ct);
        var lookupsBeforeABlock = kit.FundingLookup.Invocations.Count;
        monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(250, default, []));
        await pruner.WhenIdleAsync(ct);
        var spentAt = kit.Store.TryGetChannel(s_ab, out var ab) ? ab.SpentAtHeight : null;
        monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(321, default, []));
        await pruner.WhenIdleAsync(ct);
        var keptAt321 = kit.Store.TryGetChannel(s_ab, out _);
        monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(322, default, []));
        await pruner.WhenIdleAsync(ct);
        await pruner.StopAsync();

        // Assert: never pinned at block 0 (which would remove it at the first block, skipping BOLT 7's 72 blocks)
        Assert.Equal(0, lookupsBeforeABlock);
        Assert.Equal(250u, spentAt);
        Assert.True(keptAt321);
        Assert.False(kit.Store.TryGetChannel(s_ab, out _));
        Assert.True(kit.Store.TryGetChannel(s_bc, out _));
    }

    [Fact]
    public async Task Given_AChannelStoredAfterTheBlockThatSpentIt_When_TheNextBlockIsApplied_Then_ItIsMarkedAtTheSpendingBlock()
    {
        // Arrange: its chain check ran at tip 199 (unspent); block 200 spends it before the ingress stores it
        var kit = new GraphTestKit();
        kit.FundingFound();
        var pruner = CreatePruner(kit);
        var scid = new ShortChannelId(150, 1, 0);
        pruner.ApplyBlock(200, [SpendOf(scid)]);
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                       GraphTestKit.SignedChannelAnnouncement(scid, s_alice, s_bob,
                                                                              new TestGossipKey(11),
                                                                              new TestGossipKey(12)), 0,
                                       TestContext.Current.CancellationToken);

        // Act
        var marked = pruner.ApplyBlock(201, []);

        // Assert
        Assert.Equal(1, marked);
        Assert.True(kit.Store.TryGetChannel(scid, out var channel));
        Assert.Equal(200u, channel.SpentAtHeight);
    }

    [Fact]
    public async Task Given_ASpendOlderThanTheRecentBlocks_When_TheChannelIsStored_Then_ItIsNotMarkedByIt()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.FundingFound();
        var pruner = CreatePruner(kit);
        var scid = new ShortChannelId(150, 1, 0);
        pruner.ApplyBlock(200, [SpendOf(scid)]);
        for (var height = 201u; height <= 200 + GraphPruner.RecentSpendBlocks; height++)
            pruner.ApplyBlock(height, []);
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                       GraphTestKit.SignedChannelAnnouncement(scid, s_alice, s_bob,
                                                                              new TestGossipKey(11),
                                                                              new TestGossipKey(12)), 0,
                                       TestContext.Current.CancellationToken);

        // Act
        var marked = pruner.ApplyBlock(210, []);

        // Assert
        Assert.Equal(0, marked);
        Assert.True(kit.Store.TryGetChannel(scid, out var channel));
        Assert.Null(channel.SpentAtHeight);
    }

    [Fact]
    public async Task Given_TheFundingBlockReorgedOut_When_TheNewBranchHasAnotherTransactionThere_Then_MarkedSpentAndForgottenAfter72Blocks()
    {
        // Arrange: a reorg to 105 disconnects both funding blocks (110 and 115); at the next block the new branch is
        // not at 115 yet; it has another transaction at alice-bob's position and bob-carol's output where it was
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        var other = new TxId(Enumerable.Repeat((byte)0x42, 32).ToArray());
        kit.FundingLookup.Setup(l => l.LookupAsync(s_ab, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, other,
                                                              LightningMoney.Satoshis(5), [0x00, 0x20], 1));
        kit.FundingLookup.SetupSequence(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.Failed(FundingOutputStatus.BlockNotFound))
           .ReturnsAsync(FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, GraphTestKit.TxIdFor(s_bc),
                                                              LightningMoney.Satoshis(1_000_000), [0x00, 0x20], 1));

        // Act
        pruner.ApplyDisconnect(105);
        var first = await pruner.RecheckReorgedFundingAsync(106, TestContext.Current.CancellationToken);
        var second = await pruner.RecheckReorgedFundingAsync(116, TestContext.Current.CancellationToken);
        var third = await pruner.RecheckReorgedFundingAsync(117, TestContext.Current.CancellationToken);
        pruner.ApplyBlock(177, []);
        var keptAt177 = kit.Store.TryGetChannel(s_ab, out var ab);
        pruner.ApplyBlock(178, []);

        // Assert
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(0, third);
        Assert.True(keptAt177);
        Assert.Equal(106u, ab!.SpentAtHeight);
        Assert.False(kit.Store.TryGetChannel(s_ab, out _));
        Assert.True(kit.Store.TryGetChannel(s_bc, out var bc));
        Assert.Null(bc.SpentAtHeight);
        kit.FundingLookup.Verify(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(FundingOutputStatus.OutputSpentOrMissing, true)]
    [InlineData(FundingOutputStatus.TransactionIndexOutOfRange, true)]
    [InlineData(FundingOutputStatus.BlockUnavailable, false)]
    public async Task Given_AReorgedFundingBlock_When_TheLookupAnswers_Then_OnlyAMissingOutputMarksIt(
        FundingOutputStatus status, bool marked)
    {
        // Arrange
        var kit = await GraphStoreTests.CreateGraphAsync();
        var pruner = CreatePruner(kit);
        kit.FundingLookup.Setup(l => l.LookupAsync(It.IsAny<ShortChannelId>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(FundingOutputLookupResult.Failed(status));
        pruner.ApplyDisconnect(112);

        // Act
        var count = await pruner.RecheckReorgedFundingAsync(113, TestContext.Current.CancellationToken);
        await pruner.RecheckReorgedFundingAsync(114, TestContext.Current.CancellationToken);

        // Assert: only bob-carol (block 115) was above the fork; asked once
        Assert.Equal(marked ? 1 : 0, count);
        Assert.True(kit.Store.TryGetChannel(s_bc, out var bc));
        Assert.Equal(marked ? 113u : null, bc.SpentAtHeight);
        Assert.True(kit.Store.TryGetChannel(s_ab, out var ab));
        Assert.Null(ab.SpentAtHeight);
        kit.FundingLookup.Verify(l => l.LookupAsync(s_bc, It.IsAny<CancellationToken>()), Times.Once);
        kit.FundingLookup.Verify(l => l.LookupAsync(s_ab, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_Mainnet_When_Started_Then_NothingIsSubscribed()
    {
        // Arrange (plan D12: the graph stays off on mainnet unless Gossip:Enabled is set)
        var kit = await GraphStoreTests.CreateGraphAsync();
        var monitor = new Mock<IBlockchainMonitor>();
        var pruner = CreatePruner(kit, monitor: monitor, network: "mainnet");

        // Act
        pruner.Start();
        await pruner.StopAsync();

        // Assert
        Assert.False(pruner.IsEnabled);
        monitor.VerifyAdd(m => m.OnBlockInputs += It.IsAny<EventHandler<BlockInputsEventArgs>>(), Times.Never);
    }

    /// <summary>Rows as stored before migration <c>AddGraphFundingTxId</c> (NL-352): no funding txid.</summary>
    private static void ForgetFundingTxIds(InMemoryGraphDbRepository repository)
    {
        foreach (var (shortChannelId, channel) in repository.Channels.ToList())
            repository.Channels[shortChannelId] = channel with { FundingTxId = null };
    }

    private static (TxId TransactionId, uint OutputIndex) SpendOf(ShortChannelId shortChannelId) =>
        (GraphTestKit.TxIdFor(shortChannelId), shortChannelId.OutputIndex);

    private static GraphPruner CreatePruner(GraphTestKit kit, CompactPubKey? ourNodeId = null,
                                            Mock<IBlockchainMonitor>? monitor = null, string network = "regtest")
    {
        ISecureKeyManager? keyManager = null;
        if (ourNodeId is { } nodeId)
        {
            var mock = new Mock<ISecureKeyManager>();
            mock.Setup(k => k.GetNodePubKey()).Returns(nodeId);
            keyManager = mock.Object;
        }

        var nodeOptions = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve(network) };
        return new GraphPruner(kit.Store, (monitor ?? new Mock<IBlockchainMonitor>()).Object, kit.FundingLookup.Object,
                               Microsoft.Extensions.Options.Options.Create(kit.Options),
                               Microsoft.Extensions.Options.Options.Create(nodeOptions),
                               NullLogger<GraphPruner>.Instance, kit.Clock, keyManager);
    }
}