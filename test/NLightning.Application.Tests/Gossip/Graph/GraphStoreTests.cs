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
        foreach (var message in messages)
            Assert.Equal(GossipIngressOutcome.Accepted,
                         (await kit.Ingress.ProcessAsync(peer, message, 0, ct)).Outcome);

        return kit;
    }
}