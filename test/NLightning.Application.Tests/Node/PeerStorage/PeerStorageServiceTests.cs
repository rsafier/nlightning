namespace NLightning.Application.Tests.Node.PeerStorage;

using Application.Node.PeerStorage;
using Domain.Channels.Enums;
using Domain.Enums;
using Domain.Node.PeerStorage;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Gossip.Sync;

public class PeerStorageServiceTests
{
    private static readonly TimeSpan s_quiet = TimeSpan.FromMilliseconds(200);

    #region Provider

    [Fact]
    public async Task Given_AChannelWithThePeer_When_PeerStorageArrives_Then_ItIsStoredAndHandedBackAfterTheNextInit()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(10);
        peer.Features.OptionProvideStorage = FeatureSupport.No;
        context.AddChannel(peer.PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        var blob = new byte[] { 1, 2, 3, 4 };

        // Act
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(blob)));
        await context.Service.LastWork;
        var reconnected = new FakeGossipPeer(10);
        context.Service.OnPeerInitialized(reconnected);

        // Assert
        Assert.Equal(blob, context.Store.GetSaved(peer.PeerPubKey)!.Blob);
        var retrieval = await reconnected.NextAsync<PeerStorageRetrievalMessage>();
        Assert.Equal(blob, retrieval.Payload.Blob.ToArray());
    }

    [Fact]
    public async Task Given_AStoredBlob_When_TheNodeRestarts_Then_TheRetrievalIsTheFirstMessageAfterInit()
    {
        // Arrange: a blob stored by an earlier process
        var store = new InMemoryPeerStorageDbRepository();
        var peer = new FakeGossipPeer(11);
        await store.UpsertAsync(new StoredPeerBlob(peer.PeerPubKey, [9, 9, 9], DateTimeOffset.UnixEpoch));
        await store.SaveAsync();
        using var context = new PeerStorageTestContext(store: store);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);

        // Assert: before anything else (our own backup follows it)
        var first = await peer.NextAsync<PeerStorageRetrievalMessage>();
        Assert.Equal(new byte[] { 9, 9, 9 }, first.Payload.Blob.ToArray());
        Assert.IsType<PeerStorageMessage>(await peer.NextAsync<PeerStorageMessage>());
    }

    [Fact]
    public async Task Given_NoChannelWithThePeer_When_PeerStorageArrives_Then_ItIsIgnored()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(12);
        peer.Features.OptionProvideStorage = FeatureSupport.No;
        context.Service.OnPeerInitialized(peer);

        // Act
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await context.Service.LastWork;
        var reconnected = new FakeGossipPeer(12);
        context.Service.OnPeerInitialized(reconnected);

        // Assert
        Assert.Null(context.Store.GetSaved(peer.PeerPubKey));
        Assert.True(await reconnected.NothingSentWithinAsync(s_quiet));
    }

    [Theory]
    [InlineData(ChannelState.Closed)]
    [InlineData(ChannelState.Stale)]
    public async Task Given_OnlyAClosedChannelWithThePeer_When_PeerStorageArrives_Then_ItIsIgnored(
        ChannelState state)
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(13);
        context.AddChannel(peer.PeerPubKey, state);

        // Act
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await context.Service.LastWork;

        // Assert
        Assert.Null(await context.Service.GetStoredBlobAsync(peer.PeerPubKey));
    }

    [Fact]
    public async Task Given_StoreWithoutChannel_When_PeerStorageArrives_Then_ItIsStored()
    {
        // Arrange
        using var context = new PeerStorageTestContext(options: new PeerStorageOptions { StoreWithoutChannel = true });
        var peer = new FakeGossipPeer(14);

        // Act
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 7 })));
        await context.Service.LastWork;

        // Assert
        Assert.Equal(new byte[] { 7 }, context.Store.GetSaved(peer.PeerPubKey)!.Blob);
    }

    [Fact]
    public async Task Given_WeDoNotOfferStorage_When_PeerStorageArrives_Then_ItIsIgnored()
    {
        // Arrange
        using var context = new PeerStorageTestContext(offerStorage: FeatureSupport.No);
        var peer = new FakeGossipPeer(15);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await context.Service.LastWork;

        // Assert
        Assert.Null(context.Store.GetSaved(peer.PeerPubKey));
        Assert.Null(await context.Service.GetStoredBlobAsync(peer.PeerPubKey));
    }

    [Fact]
    public async Task Given_TwoBlobsWithinAMinute_When_Stored_Then_TheLatestIsHandedBackAndWrittenAfterTheDelay()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(16);
        context.AddChannel(peer.PeerPubKey);
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await context.Service.LastWork;

        // Act: a second one 10 s later (BOLT 1 MAY delay to one update per minute)
        context.Time.Advance(TimeSpan.FromSeconds(10));
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 2 })));
        await context.Service.LastWork;
        var savedBeforeTheMinute = context.Store.GetSaved(peer.PeerPubKey)!.Blob;
        var handedBack = await context.Service.GetStoredBlobAsync(peer.PeerPubKey);
        context.Time.Advance(TimeSpan.FromSeconds(50));
        await context.Service.RunRoundAsync();

        // Assert: MUST replace the old blob with the latest
        Assert.Equal(new byte[] { 1 }, savedBeforeTheMinute);
        Assert.Equal(new byte[] { 2 }, handedBack!.Blob);
        Assert.Equal(new byte[] { 2 }, context.Store.GetSaved(peer.PeerPubKey)!.Blob);
        Assert.Equal(2, context.Store.Saves);
    }

    [Fact]
    public async Task Given_ADelayedWrite_When_TheServiceIsDisposed_Then_ItIsWritten()
    {
        // Arrange
        var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(17);
        context.AddChannel(peer.PeerPubKey);
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await context.Service.LastWork;
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 3 })));

        // Act
        context.Dispose();

        // Assert
        Assert.Equal(new byte[] { 3 }, context.Store.GetSaved(peer.PeerPubKey)!.Blob);
    }

    [Fact]
    public async Task Given_AFailedWrite_When_TheNextRoundRuns_Then_TheBlobIsWritten()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(18);
        context.AddChannel(peer.PeerPubKey);
        context.Store.FailNextSaves = 1;

        // Act
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 5 })));
        await context.Service.LastWork;
        var afterFailure = context.Store.GetSaved(peer.PeerPubKey);
        context.Time.Advance(TimeSpan.FromMinutes(1));
        await context.Service.RunRoundAsync();

        // Assert
        Assert.Null(afterFailure);
        Assert.Equal(new byte[] { 5 }, context.Store.GetSaved(peer.PeerPubKey)!.Blob);
    }

    #endregion

    #region Client

    [Fact]
    public async Task Given_APeerThatOffersStorage_When_Connected_Then_OurEncryptedBackupIsSent()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(20);
        var channel = context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);

        // Assert: padded to the BOLT 1 maximum, and only we can read it
        var sent = await peer.NextAsync<PeerStorageMessage>();
        Assert.Equal(PeerStorageConstants.MaxBlobLength, sent.Payload.Blob.Length);
        var contents = await context.BlobProvider.TryReadBlobAsync(sent.Payload.Blob,
                                                                   TestContext.Current.CancellationToken);
        Assert.Equal(channel.ChannelId, Assert.Single(contents!.Channels).ChannelId);
    }

    [Fact]
    public async Task Given_APeerThatDoesNotOfferStorage_When_Connected_Then_NothingIsSent()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(21);
        peer.Features.OptionProvideStorage = FeatureSupport.No;
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);

        // Assert
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_SendBackupsOff_When_APeerThatOffersStorageConnects_Then_NothingIsSent()
    {
        // Arrange
        using var context = new PeerStorageTestContext(options: new PeerStorageOptions { SendBackups = false });
        var peer = new FakeGossipPeer(22);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);

        // Assert
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_OurChannelsUnchanged_When_ARoundRuns_Then_TheBackupIsNotSentAgainButAChangeIs()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(23);
        context.AddChannel(peer.PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        await peer.NextAsync<PeerStorageMessage>();

        // Act
        context.Time.Advance(TimeSpan.FromMinutes(1));
        await context.Service.RunRoundAsync();
        var unchangedSentNothing = await peer.NothingSentWithinAsync(s_quiet);
        var added = context.AddChannel(new FakeGossipPeer(24).PeerPubKey);
        context.Time.Advance(TimeSpan.FromMinutes(1));
        await context.Service.RunRoundAsync();

        // Assert
        Assert.True(unchangedSentNothing);
        var update = await peer.NextAsync<PeerStorageMessage>();
        var contents = await context.BlobProvider.TryReadBlobAsync(update.Payload.Blob,
                                                                   TestContext.Current.CancellationToken);
        Assert.Contains(contents!.Channels, c => c.ChannelId == added.ChannelId);
        Assert.Equal(2, contents.Channels.Count);
    }

    [Fact]
    public async Task Given_ADisconnectedPeer_When_OurChannelsChange_Then_NothingIsSentToIt()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(25);
        context.AddChannel(peer.PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        await peer.NextAsync<PeerStorageMessage>();
        peer.Disconnect();

        // Act
        context.AddChannel(new FakeGossipPeer(26).PeerPubKey);
        await context.Service.RunRoundAsync();

        // Assert
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_OurBackupHandedBack_When_ItNamesChannelsWeDoNotKnow_Then_ADataLossRetrievalIsRecorded()
    {
        // Arrange: the peer kept our backup, then we lost a channel's record (restored an old database)
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(27);
        var kept = context.AddChannel(peer.PeerPubKey);
        var lost = context.AddChannel(new FakeGossipPeer(28).PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        var backup = await peer.NextAsync<PeerStorageMessage>();
        context.KnownChannelIds.Remove(lost.ChannelId);

        // Act
        context.Service.HandleMessage(
            peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(backup.Payload.Blob)));
        await context.Service.LastWork;

        // Assert
        var retrieval = Assert.Single(context.Service.GetRetrievals());
        Assert.Equal(peer.PeerPubKey, retrieval.PeerNodeId);
        Assert.True(retrieval.MatchesLastSent);
        Assert.Equal(2, retrieval.Contents!.Channels.Count);
        var unknown = Assert.Single(retrieval.UnknownChannels);
        Assert.Equal(lost.ChannelId, unknown.ChannelId);
        Assert.Equal(lost.RemoteNodeId, unknown.PeerNodeId);
        Assert.DoesNotContain(retrieval.UnknownChannels, c => c.ChannelId == kept.ChannelId);
    }

    [Fact]
    public async Task Given_AnotherNodesBlob_When_HandedBack_Then_ItIsRecordedAsNotOurs()
    {
        // Arrange
        using var context = new PeerStorageTestContext(nodeSeed: 1);
        using var other = new PeerStorageTestContext(nodeSeed: 2);
        other.AddChannel(new FakeGossipPeer(29).PeerPubKey);
        var foreign = await other.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var peer = new FakeGossipPeer(30);

        // Act
        context.Service.HandleMessage(
            peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(foreign!.Blob)));
        await context.Service.LastWork;

        // Assert
        var retrieval = Assert.Single(context.Service.GetRetrievals());
        Assert.Null(retrieval.Contents);
        Assert.Null(retrieval.MatchesLastSent);
        Assert.Empty(retrieval.UnknownChannels);
    }

    #endregion
}