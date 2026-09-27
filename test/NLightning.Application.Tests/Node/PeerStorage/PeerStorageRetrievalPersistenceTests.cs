namespace NLightning.Application.Tests.Node.PeerStorage;

using Domain.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Gossip.Sync;

/// <summary>
/// NL-432: the latest <c>peer_storage_retrieval</c> of each peer is kept across restarts and listed for the operator.
/// </summary>
public class PeerStorageRetrievalPersistenceTests
{
    [Fact]
    public async Task Given_ADataLossRetrieval_When_TheNodeRestarts_Then_ItIsListedWithTheChannelsToRestore()
    {
        // Arrange: the peer kept our backup naming two channels; the record of one is lost
        var retrievals = new InMemoryPeerStorageRetrievalDbRepository();
        var peer = new FakeGossipPeer(60);
        var lostPeer = new FakeGossipPeer(61).PeerPubKey;
        byte[] keptByPeer;
        Domain.Channels.ValueObjects.ChannelId keptId;
        Domain.Channels.ValueObjects.ChannelId lostId;
        using (var context = new PeerStorageTestContext(retrievals: retrievals))
        {
            keptId = context.AddChannel(peer.PeerPubKey).ChannelId;
            lostId = context.AddChannel(lostPeer).ChannelId;
            keptByPeer = (await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;
            context.KnownChannelIds.Remove(lostId);

            // Act
            context.Service.HandleMessage(
                peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(keptByPeer)));
            await context.Service.LastWork;
        }

        // A new process over the same tables: nothing handed back in this process yet
        using var restarted = new PeerStorageTestContext(retrievals: retrievals);
        restarted.KnownChannelIds.Add(keptId);
        var listed = await restarted.Service.ListRetrievalsAsync(TestContext.Current.CancellationToken);

        // Assert: the row holds the peer's blob byte for byte and the lost channel
        var saved = retrievals.GetSaved(peer.PeerPubKey);
        Assert.NotNull(saved);
        Assert.Equal(keptByPeer, saved.Blob);
        Assert.Equal(lostId, Assert.Single(saved.UnknownChannelIds));
        Assert.Null(saved.MatchesLastSent);
        Assert.Empty(restarted.Service.GetRetrievals());

        var report = Assert.Single(listed);
        Assert.Equal(peer.PeerPubKey, report.PeerNodeId);
        Assert.True(report.Persisted);
        Assert.NotNull(report.Contents);
        Assert.Equal(keptByPeer, report.Blob);
        Assert.Equal(2, report.Channels.Count);
        var lost = Assert.Single(report.StillUnknown);
        Assert.Equal(lostId, lost.ChannelId);
        Assert.Equal(lostPeer, lost.PeerNodeId);
        Assert.True(lost.UnknownWhenReceived);
        var kept = Assert.Single(report.Channels, c => c.KnownNow);
        Assert.Equal(keptId, kept.ChannelId);
        Assert.False(kept.UnknownWhenReceived);
    }

    [Fact]
    public async Task Given_ALostChannelRestoredLater_When_Listed_Then_ItIsKnownNowAndWasUnknownWhenReceived()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(62);
        context.AddChannel(peer.PeerPubKey);
        var lost = context.AddChannel(new FakeGossipPeer(63).PeerPubKey);
        var blob = (await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;
        context.KnownChannelIds.Remove(lost.ChannelId);
        context.Service.HandleMessage(peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(blob)));
        await context.Service.LastWork;

        // Act: restorechanbackup brought the channel's record back
        context.KnownChannelIds.Add(lost.ChannelId);
        var report = Assert.Single(await context.Service.ListRetrievalsAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(report.StillUnknown);
        var restored = Assert.Single(report.Channels, c => c.ChannelId == lost.ChannelId);
        Assert.True(restored.UnknownWhenReceived);
        Assert.True(restored.KnownNow);
    }

    [Fact]
    public async Task Given_AFailedRetrievalWrite_When_Listed_Then_ItComesFromMemoryAndTheNextRoundWritesIt()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(64);
        context.AddChannel(peer.PeerPubKey);
        context.Retrievals.FailNextSaves = 1;
        var blob = (await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;

        // Act
        context.Service.HandleMessage(peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(blob)));
        await context.Service.LastWork;
        var afterFailure = context.Retrievals.GetSaved(peer.PeerPubKey);
        var listedBefore = Assert.Single(await context.Service.ListRetrievalsAsync(
                                             TestContext.Current.CancellationToken));
        await context.Service.RunRoundAsync();
        var listedAfter = Assert.Single(await context.Service.ListRetrievalsAsync(
                                            TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(afterFailure);
        Assert.False(listedBefore.Persisted);
        Assert.Equal(blob, listedBefore.Blob);
        Assert.Equal(blob, context.Retrievals.GetSaved(peer.PeerPubKey)!.Blob);
        Assert.True(listedAfter.Persisted);
    }

    [Fact]
    public async Task Given_AnotherNodesBlob_When_HandedBack_Then_ItIsKeptAndListedAsNotOurs()
    {
        // Arrange
        using var context = new PeerStorageTestContext(nodeSeed: 1);
        using var other = new PeerStorageTestContext(nodeSeed: 2);
        other.AddChannel(new FakeGossipPeer(65).PeerPubKey);
        var foreign = (await other.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;
        var peer = new FakeGossipPeer(66);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.HandleMessage(peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(foreign)));
        await context.Service.LastWork;
        var report = Assert.Single(await context.Service.ListRetrievalsAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(report.Contents);
        Assert.Empty(report.Channels);
        Assert.Equal(foreign, report.Blob);
        Assert.Empty(context.Retrievals.GetSaved(peer.PeerPubKey)!.UnknownChannelIds);
    }

    [Fact]
    public async Task Given_TwoRetrievalsOfOnePeer_When_Listed_Then_OnlyTheLatestIsKept()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(67);
        context.AddChannel(peer.PeerPubKey);
        var first = (await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;
        context.AddChannel(new FakeGossipPeer(68).PeerPubKey);
        var second = (await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;

        // Act
        context.Service.HandleMessage(peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(first)));
        await context.Service.LastWork;
        context.Time.Advance(TimeSpan.FromMinutes(5));
        context.Service.HandleMessage(peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(second)));
        await context.Service.LastWork;

        // Assert
        var report = Assert.Single(await context.Service.ListRetrievalsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(second, report.Blob);
        Assert.Equal(2, report.Channels.Count);
        Assert.Equal(context.Time.GetUtcNow(), report.ReceivedAt);
    }

    [Fact]
    public async Task Given_BlobsWeKeepForPeers_When_Listed_Then_TheLatestOfEachIsReturnedIncludingADelayedWrite()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peerA = new FakeGossipPeer(69);
        var peerB = new FakeGossipPeer(70);
        peerA.Features.OptionProvideStorage = FeatureSupport.No;
        context.AddChannel(peerA.PeerPubKey);
        context.AddChannel(peerB.PeerPubKey);

        // Act: peer A's second blob comes within a minute, so its write is delayed
        context.Service.HandleMessage(peerA, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await context.Service.LastWork;
        context.Service.HandleMessage(peerA, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 2, 2 })));
        context.Service.HandleMessage(peerB, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 3 })));
        await context.Service.LastWork;
        var listed = await context.Service.ListStoredBlobsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, listed.Count);
        Assert.Equal(new byte[] { 2, 2 }, Assert.Single(listed, b => b.PeerNodeId == peerA.PeerPubKey).Blob);
        Assert.Equal(new byte[] { 3 }, Assert.Single(listed, b => b.PeerNodeId == peerB.PeerPubKey).Blob);
        Assert.Equal(new byte[] { 1 }, context.Store.GetSaved(peerA.PeerPubKey)!.Blob);
        Assert.Equal(listed.OrderBy(b => Convert.ToHexString(b.PeerNodeId)).ToList(), listed);
    }
}