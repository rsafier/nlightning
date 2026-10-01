namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;

public class GraphStoreTests
{
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_carol = new(3);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Given_AGraph_When_FlushedAndLoadedIntoANewStore_Then_TheSnapshotsAreEqual()
    {
        // Arrange
        var kit = await CreateGraphAsync();
        var before = kit.Store.GetSnapshot();

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var restarted = new GraphTestKit(kit.Repository);
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        GraphTestKit.AssertSameGraph(before, restarted.Store.GetSnapshot());
        Assert.True(restarted.Store.IsLoaded);
        Assert.Equal(0, restarted.Store.PendingChanges);
    }

    [Fact]
    public async Task Given_ChannelsWithFundingTxIds_When_FlushedAndLoadedIntoANewStore_Then_TheTxIdsAreKnownWithoutALookup()
    {
        // Arrange: NL-352, the chain check gave each channel its funding txid
        var kit = await CreateGraphAsync();
        var ab = new ShortChannelId(110, 1, 0);
        var bc = new ShortChannelId(115, 1, 0);

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var restarted = new GraphTestKit(kit.Repository);
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert: saved with the channel, and back in the funding-outpoint index after the load
        Assert.Equal(GraphTestKit.TxIdFor(ab), kit.Repository.Channels[ab].FundingTxId);
        Assert.Empty(restarted.Store.GetChannelsWithoutFundingTxId());
        Assert.True(restarted.Store.TryGetFundingTxId(bc, out var fundingTxId));
        Assert.Equal(GraphTestKit.TxIdFor(bc), fundingTxId);
        Assert.True(restarted.Store.TryGetChannelByFundingOutpoint(GraphTestKit.TxIdFor(ab), ab.OutputIndex,
                                                                   out var found));
        Assert.Equal(ab, found);
    }

    [Fact]
    public async Task Given_AChannelStoredWithoutFundingTxId_When_TheTxIdIsSet_Then_TheNextFlushSavesIt()
    {
        // Arrange: a row from before the column (or an unverified channel) has none
        var kit = await CreateGraphAsync();
        var ab = new ShortChannelId(110, 1, 0);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        kit.Repository.Channels[ab] = kit.Repository.Channels[ab] with { FundingTxId = null };
        var restarted = new GraphTestKit(kit.Repository);
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal([ab], restarted.Store.GetChannelsWithoutFundingTxId());

        // Act: the pruner's lookup found it; setting the same txid again changes nothing
        Assert.True(restarted.Store.TrySetFundingTxId(ab, GraphTestKit.TxIdFor(ab)));
        var pending = restarted.Store.PendingChanges;
        await restarted.Store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(restarted.Store.TrySetFundingTxId(ab, GraphTestKit.TxIdFor(ab)));

        // Assert
        Assert.Equal(1, pending);
        Assert.Equal(0, restarted.Store.PendingChanges);
        Assert.Equal(GraphTestKit.TxIdFor(ab), kit.Repository.Channels[ab].FundingTxId);
        var again = new GraphTestKit(kit.Repository);
        await again.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(again.Store.GetChannelsWithoutFundingTxId());
    }

    [Fact]
    public async Task Given_ManyUpdatesToOneChannel_When_Flushed_Then_OnlyTheLatestPolicyIsWrittenInOneSave()
    {
        // Arrange
        var kit = await CreateGraphAsync();
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var saves = kit.Repository.Saves;
        var scid = new ShortChannelId(110, 1, 0);
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        for (uint i = 1; i <= 5; i++)
            Assert.True(kit.Store.TryApplyPolicy(scid, Policy(s_now + i, feeBase: i)));

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(saves + 1, kit.Repository.Saves);
        Assert.Equal(5U, kit.Repository.Policies[(scid, direction)].FeeBaseMsat);
    }

    [Fact]
    public async Task Given_AFailedSave_When_FlushedAgain_Then_TheChangesAreWrittenThen()
    {
        // Arrange
        var kit = await CreateGraphAsync();
        kit.Repository.FailNextSave = true;

        // Act
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var pendingAfterFailure = kit.Store.PendingChanges;
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(pendingAfterFailure > 0);
        Assert.Equal(0, kit.Store.PendingChanges);
        Assert.Equal(2, kit.Repository.Channels.Count);
        Assert.Equal(2, kit.Repository.Nodes.Count);
    }

    [Fact]
    public async Task Given_RemovedChannelAndNode_When_Flushed_Then_TheyAreDeletedFromTheDatabase()
    {
        // Arrange
        var kit = await CreateGraphAsync();
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var scid = new ShortChannelId(110, 1, 0);

        // Act
        Assert.True(kit.Store.RemoveChannel(scid));
        Assert.True(kit.Store.RemoveNode(s_carol.PubKey));
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(scid, kit.Repository.Channels.Keys);
        Assert.DoesNotContain(kit.Repository.Policies.Keys, k => k.Item1 == scid);
        Assert.DoesNotContain(s_carol.PubKey, kit.Repository.Nodes.Keys);
        Assert.False(kit.Store.NodeHasChannels(s_alice.PubKey));
        Assert.True(kit.Store.NodeHasChannels(s_bob.PubKey));
    }

    [Fact]
    public async Task Given_ChangesMadeBeforeTheLoad_When_Loaded_Then_TheyWinOverTheDatabase()
    {
        // Arrange: the database has a channel and alice's announcement at s_now
        var kit = await CreateGraphAsync();
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var restarted = new GraphTestKit(kit.Repository);
        restarted.FundingFound();
        var newer = GraphTestKit.SignedNodeAnnouncement(s_carol, s_now + 100, "newer");
        await restarted.Ingress.ApplyOwnAsync(
            GraphTestKit.SignedChannelAnnouncement(new ShortChannelId(120, 1, 0), s_carol, s_alice,
                                                   new TestGossipKey(13), new TestGossipKey(11)),
            null, TestContext.Current.CancellationToken);
        await restarted.Ingress.ApplyOwnAsync(newer, null, TestContext.Current.CancellationToken);

        // Act
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(restarted.Store.TryGetNode(s_carol.PubKey, out var node));
        Assert.Equal("newer", node.AliasText);
        Assert.Equal(3, restarted.Store.ChannelCount);
    }

    [Fact]
    public async Task Given_ABan_When_FlushedAndReloaded_Then_TheNodeIsStillBannedUntilItEnds()
    {
        // Arrange
        var kit = new GraphTestKit();
        kit.Store.Ban(s_alice.PubKey, "test", GraphTestKit.DefaultNow.AddHours(1));
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Act
        var restarted = new GraphTestKit(kit.Repository);
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);
        var bannedNow = restarted.Store.IsBanned(s_alice.PubKey);
        restarted.Clock.Now = GraphTestKit.DefaultNow.AddHours(2);

        // Assert
        Assert.True(bannedNow);
        Assert.False(restarted.Store.IsBanned(s_alice.PubKey));
    }

    [Fact]
    public async Task Given_AnExpiredBan_When_Flushed_Then_ItIsForgottenAndItsRowDeleted()
    {
        // Arrange (NL-372): the ban is stored, then ends
        var kit = new GraphTestKit();
        kit.Store.Ban(s_alice.PubKey, "test", GraphTestKit.DefaultNow.AddHours(1));
        kit.Store.Ban(s_bob.PubKey, "test", GraphTestKit.DefaultNow.AddHours(5));
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, kit.Repository.Bans.Count);

        // Act
        kit.Clock.Now = GraphTestKit.DefaultNow.AddHours(2);
        Assert.False(kit.Store.IsBanned(s_alice.PubKey));
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert: only alice's row is gone; bob's still lasts
        Assert.False(kit.Store.IsBanned(s_alice.PubKey));
        Assert.Equal([s_bob.PubKey], kit.Repository.Bans.Keys);
        Assert.Equal(0, kit.Store.PendingChanges);
    }

    [Fact]
    public async Task Given_ExpiredBanRows_When_LoadedIntoANewStoreAndFlushed_Then_TheRowsAreDeleted()
    {
        // Arrange: a row that ended while the node was down (the load reads only the bans that still last)
        var kit = new GraphTestKit();
        kit.Store.Ban(s_alice.PubKey, "test", GraphTestKit.DefaultNow.AddHours(1));
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        kit.Clock.Now = GraphTestKit.DefaultNow.AddHours(2);
        var restarted = new GraphTestKit(kit.Repository, now: GraphTestKit.DefaultNow.AddHours(2));
        await restarted.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(kit.Repository.Bans);

        // Act: the first flush after the load, with no other change pending
        await restarted.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(kit.Repository.Bans);
        Assert.Equal(0, restarted.Store.PendingChanges);
    }

    [Fact]
    public async Task Given_SpentChannel_When_TheSpendIsReorgedOut_Then_ItIsUnspentAgainAndPersistedSo()
    {
        // Arrange
        var kit = await CreateGraphAsync();
        var scid = new ShortChannelId(110, 1, 0);
        Assert.True(kit.Store.MarkSpent(scid, 300));
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(300U, kit.Repository.Channels[scid].SpentAtHeight);

        // Act
        var cleared = kit.Store.ClearSpentAbove(299);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, cleared);
        Assert.True(kit.Store.TryGetChannel(scid, out var channel));
        Assert.Null(channel.SpentAtHeight);
        Assert.Null(kit.Repository.Channels[scid].SpentAtHeight);
    }

    [Fact]
    public async Task Given_AnOlderPolicyOrAnnouncement_When_Applied_Then_TheStoredOneStays()
    {
        // Arrange
        var kit = await CreateGraphAsync();
        var scid = new ShortChannelId(110, 1, 0);

        // Act
        var olderPolicy = kit.Store.TryApplyPolicy(scid, Policy(s_now - 1_000));
        var unknownChannel = kit.Store.TryApplyPolicy(new ShortChannelId(999, 1, 0), Policy(s_now + 1));
        var olderNode = kit.Store.TryApplyNode(ToNode(GraphTestKit.SignedNodeAnnouncement(s_alice, s_now - 1)));

        // Assert
        Assert.False(olderPolicy);
        Assert.False(unknownChannel);
        Assert.False(olderNode);
    }

    [Fact]
    public async Task Given_OurOwnAnnouncedChannel_When_ItsFundingMoves_Then_ForgetOwnChannelRemovesItAndItsRow()
    {
        // Arrange (NL-362): our channel entered the graph through the sink (as ours, without a chain lookup); another
        // channel was announced there by a peer
        var kit = new GraphTestKit(ourNodeId: s_alice.PubKey);
        kit.FundingFound();
        var ct = TestContext.Current.CancellationToken;
        var ours = new ShortChannelId(120, 1, 0);
        var theirs = new ShortChannelId(115, 1, 0);
        await kit.Ingress.ApplyOwnAsync(
            GraphTestKit.SignedChannelAnnouncement(ours, s_alice, s_bob, new TestGossipKey(11),
                                                   new TestGossipKey(12)), null, ct);
        await kit.Ingress.ApplyOwnAsync(
            GraphTestKit.SignedChannelUpdate(ours, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob), s_now), null,
            ct);
        var peer = GraphTestKit.CreatePeer().Object;
        Assert.Equal(GossipIngressOutcome.Pending,
                     (await kit.Ingress.ProcessAsync(peer,
                                                     GraphTestKit.SignedChannelAnnouncement(theirs, s_bob, s_carol,
                                                                                            new TestGossipKey(12),
                                                                                            new TestGossipKey(13)),
                                                     0, ct)).Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await kit.Ingress.ProcessAsync(peer,
                                                     GraphTestKit.SignedChannelUpdate(
                                                         theirs, s_carol, GraphTestKit.DirectionOf(s_carol, s_bob),
                                                         s_now), 0, ct)).Outcome);
        Assert.Equal(2, kit.Store.ChannelCount);
        await kit.Store.FlushAsync(ct);
        Assert.Equal(2, kit.Repository.Channels.Count);

        // Act: the reorg moved our funding; the handler forgets the old scid through the sink
        kit.Ingress.ForgetOwnChannel(ours);
        await kit.Ingress.WhenOwnGossipAppliedAsync(ct);
        await kit.Ingress.StopAsync();

        // Assert: our announcement and its policies are gone (memory and row); the peer's channel stays
        Assert.False(kit.Store.TryGetChannel(ours, out _));
        Assert.False(kit.Repository.Channels.ContainsKey(ours));
        Assert.True(kit.Store.TryGetChannel(theirs, out _));
        Assert.Equal(1, kit.Store.ChannelCount);
    }

    [Fact]
    public async Task Given_APeerAnnouncedChannel_When_ForgetOwnChannelIsCalled_Then_ItStays()
    {
        // Arrange: the same short channel id announced by others (Verified: its funding was checked)
        var kit = await CreateGraphAsync();
        var ct = TestContext.Current.CancellationToken;
        var scid = new ShortChannelId(110, 1, 0);

        // Act
        kit.Ingress.ForgetOwnChannel(scid);
        await kit.Ingress.WhenOwnGossipAppliedAsync(ct);
        await kit.Ingress.StopAsync();

        // Assert: only our own channels are forgotten (NL-362)
        Assert.True(kit.Store.TryGetChannel(scid, out _));
        Assert.Equal(2, kit.Store.ChannelCount);
    }

    [Fact]
    public async Task Given_WritersChangingTheGraphWhileASnapshotIsBuilt_Then_EverySnapshotIsAConsistentView()
    {
        // Arrange (NL-374): the snapshot build runs outside the writer lock, so writers go on while it runs
        var kit = await CreateGraphAsync();
        var ct = TestContext.Current.CancellationToken;
        var last = new ShortChannelId(599, 1, 0);

        // Act: a writer churns channels while the reader rebuilds the snapshot over and over
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                var scid = new ShortChannelId((uint)(200 + i), 1, 0);
                Assert.True(kit.Store.TryAddChannel(new GraphChannel(scid, s_alice.PubKey, s_bob.PubKey,
                                                                     new TestGossipKey(11).PubKey,
                                                                     new TestGossipKey(12).PubKey, 1_000)));
                Assert.True(kit.Store.TryApplyNode(ToNode(GraphTestKit.SignedNodeAnnouncement(
                    s_bob, s_now + (uint)i, $"bob{i}"))));
                if (i > 0)
                    Assert.True(kit.Store.RemoveChannel(new ShortChannelId((uint)(199 + i), 1, 0)));
            }
        }, ct);

        for (var round = 0; round < 200; round++)
        {
            var snapshot = kit.Store.GetSnapshot();

            // Assert: one graph version, indexes and contents in agreement (NodeCount also counts the
            // unannounced ends of channels, Nodes only the announced ones)
            Assert.Equal(snapshot.Channels.Count(), snapshot.ChannelCount);
            Assert.True(snapshot.NodeCount >= snapshot.Nodes.Count());
            foreach (var channel in snapshot.Channels)
                Assert.True(snapshot.TryGetChannel(channel.ShortChannelId, out _));
            foreach (var node in snapshot.Nodes)
            {
                Assert.True(snapshot.TryGetNodeIndex(node.NodeId, out var index));
                Assert.Same(node, snapshot.GetNode(index));
                foreach (var adjacency in snapshot.GetAdjacency(index))
                    Assert.True(snapshot.TryGetChannel(adjacency.Channel.ShortChannelId, out _));
            }
        }

        await writer;

        // Assert: after the writers ended the next snapshot is up to date
        Assert.Equal(kit.Store.ChannelCount, kit.Store.GetSnapshot().ChannelCount);
        Assert.True(kit.Store.GetSnapshot().TryGetChannel(last, out _));
    }

    private static GraphPolicy Policy(uint timestamp, uint feeBase = 1_000)
    {
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var update = GraphTestKit.SignedChannelUpdate(new ShortChannelId(110, 1, 0), s_alice, direction, timestamp,
                                                      feeBase).Payload;
        return GraphPolicy.FromChannelUpdate(update) with { RawUpdate = update.GetBytes() };
    }

    private static GraphNode ToNode(Domain.Protocol.Messages.NodeAnnouncementMessage message) =>
        new(message.Payload.NodeId, message.Payload.Timestamp, message.Payload.Features, message.Payload.Alias.Span,
            message.Payload.RgbColor.Span)
        { RawAnnouncement = message.Payload.GetBytes() };

    /// <summary>alice-bob (110x1x0, both policies) and bob-carol (115x1x0, one policy); alice and carol announced.</summary>
    internal static async Task<GraphTestKit> CreateGraphAsync(GraphTestKit? kit = null)
    {
        kit ??= new GraphTestKit();
        kit.FundingFound();
        var peer = GraphTestKit.CreatePeer().Object;
        var ct = TestContext.Current.CancellationToken;
        var ab = new ShortChannelId(110, 1, 0);
        var bc = new ShortChannelId(115, 1, 0);
        var messages = new List<Domain.Protocol.Interfaces.IMessage>
        {
            GraphTestKit.SignedChannelAnnouncement(ab, s_alice, s_bob, new TestGossipKey(11), new TestGossipKey(12)),
            GraphTestKit.SignedChannelAnnouncement(bc, s_bob, s_carol, new TestGossipKey(12), new TestGossipKey(13)),
            GraphTestKit.SignedChannelUpdate(ab, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob), s_now),
            GraphTestKit.SignedChannelUpdate(ab, s_bob, GraphTestKit.DirectionOf(s_bob, s_alice), s_now, 2_000),
            GraphTestKit.SignedChannelUpdate(bc, s_carol, GraphTestKit.DirectionOf(s_carol, s_bob), s_now),
            GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, "alice"),
            GraphTestKit.SignedNodeAnnouncement(s_carol, s_now, "carol")
        };
        // NL-406: each announcement waits until its first update, which follows in the list
        foreach (var message in messages)
            Assert.Equal(message is Domain.Protocol.Messages.ChannelAnnouncementMessage
                             ? GossipIngressOutcome.Pending
                             : GossipIngressOutcome.Accepted,
                         (await kit.Ingress.ProcessAsync(peer, message, 0, ct)).Outcome);

        return kit;
    }
}