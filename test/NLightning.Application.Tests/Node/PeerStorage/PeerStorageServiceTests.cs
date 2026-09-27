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
    public async Task Given_AFailedLoad_When_ThePeerReconnects_Then_TheLoadIsRetriedAndTheBlobHandedBack()
    {
        // Arrange: a blob stored by an earlier process, and a database that fails the first load
        var store = new InMemoryPeerStorageDbRepository();
        var peer = new FakeGossipPeer(12);
        peer.Features.OptionProvideStorage = FeatureSupport.No;
        await store.UpsertAsync(new StoredPeerBlob(peer.PeerPubKey, [7, 7], DateTimeOffset.UnixEpoch));
        await store.SaveAsync();
        store.FailNextLoads = 1;
        using var context = new PeerStorageTestContext(store: store);
        context.Service.OnPeerInitialized(peer);

        // Act
        var reconnected = new FakeGossipPeer(12);
        reconnected.Features.OptionProvideStorage = FeatureSupport.No;
        context.Service.OnPeerInitialized(reconnected);

        // Assert
        var retrieval = await reconnected.NextAsync<PeerStorageRetrievalMessage>();
        Assert.Equal(new byte[] { 7, 7 }, retrieval.Payload.Blob.ToArray());
        Assert.NotNull(await context.Service.GetStoredBlobAsync(peer.PeerPubKey));
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

    [Fact]
    public async Task Given_PeerStorageBeforeTheChannel_When_TheChannelExistsAtTheNextRound_Then_TheBlobIsKept()
    {
        // Arrange: CLN sends its blob right after init, before our open_channel (wave rf1 CLN proof)
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(16);
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 4, 2 })));
        await context.Service.LastWork;
        Assert.Null(await context.Service.GetStoredBlobAsync(peer.PeerPubKey));
        context.AddChannel(peer.PeerPubKey);

        // Act
        await context.Service.RunRoundAsync();
        await context.Service.LastWork;

        // Assert
        Assert.Equal(new byte[] { 4, 2 }, (await context.Service.GetStoredBlobAsync(peer.PeerPubKey))!.Blob);
        Assert.Equal(new byte[] { 4, 2 }, context.Store.GetSaved(peer.PeerPubKey)!.Blob);
    }

    [Fact]
    public async Task Given_AHeldBlobWhoseChannelNeverCame_When_ItsLifetimePassed_Then_ALaterChannelDoesNotKeepIt()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(17);
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 9 })));
        context.Time.Advance(PeerStorageService.PendingWithoutChannelLifetime + TimeSpan.FromSeconds(1));
        await context.Service.RunRoundAsync();
        context.AddChannel(peer.PeerPubKey);

        // Act
        await context.Service.RunRoundAsync();
        await context.Service.LastWork;

        // Assert
        Assert.Null(await context.Service.GetStoredBlobAsync(peer.PeerPubKey));
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
    public async Task Given_TheSameBlobAgain_When_Received_Then_NothingIsWritten()
    {
        // Arrange: peers send their blob on every connection
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(19);
        context.AddChannel(peer.PeerPubKey);
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 4, 2 })));
        await context.Service.LastWork;

        // Act
        context.Time.Advance(TimeSpan.FromMinutes(5));
        context.Service.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 4, 2 })));
        await context.Service.RunRoundAsync();

        // Assert
        Assert.Equal(1, context.Store.Saves);
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
    public async Task Given_OurBackupAlreadySent_When_ThePeerReconnects_Then_ItIsNotSentAgainAndItsRetrievalMatches()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(31);
        context.AddChannel(peer.PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        var sent = await peer.NextAsync<PeerStorageMessage>();
        peer.Disconnect();
        var reconnected = new FakeGossipPeer(31);

        // Act: the peer hands back what it keeps right after init
        context.Service.OnPeerInitialized(reconnected);
        context.Service.HandleMessage(
            reconnected, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(sent.Payload.Blob)));
        await context.Service.LastWork;

        // Assert
        Assert.True(await reconnected.NothingSentWithinAsync(s_quiet));
        Assert.True(Assert.Single(context.Service.GetRetrievals()).MatchesLastSent);
    }

    [Fact]
    public async Task Given_ARetrievalThatIsNotOurLastBlob_When_Received_Then_TheCurrentBackupIsSentAgain()
    {
        // Arrange: the peer hands back an older backup of ours
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(32);
        context.AddChannel(peer.PeerPubKey);
        var older = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        context.AddChannel(new FakeGossipPeer(34).PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        await peer.NextAsync<PeerStorageMessage>();

        // Act
        context.Service.HandleMessage(
            peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(older!.Blob)));
        await context.Service.LastWork;

        // Assert
        Assert.False(Assert.Single(context.Service.GetRetrievals()).MatchesLastSent);
        var resent = await peer.NextAsync<PeerStorageMessage>();
        Assert.NotNull(await context.BlobProvider.TryReadBlobAsync(resent.Payload.Blob,
                                                                   TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ARetrievalHoldingOurCurrentBackup_When_ItIsAnotherEncryption_Then_ItIsNotSentAgain()
    {
        // Arrange: what the peer keeps from before our restart holds the same channels as our current backup
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(33);
        context.AddChannel(peer.PeerPubKey);
        var beforeRestart = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        context.Service.OnPeerInitialized(peer);
        await peer.NextAsync<PeerStorageMessage>();

        // Act
        context.Service.HandleMessage(
            peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(beforeRestart!.Blob)));
        await context.Service.LastWork;

        // Assert
        Assert.False(Assert.Single(context.Service.GetRetrievals()).MatchesLastSent);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
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
        context.AddChannel(peer.PeerPubKey);

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

    [Fact]
    public async Task Given_AStrangerWithoutChannel_When_ItSendsARetrieval_Then_NothingIsRecorded()
    {
        // Arrange: any node id can connect and send one
        using var context = new PeerStorageTestContext();
        var stranger = new FakeGossipPeer(35);

        // Act
        context.Service.HandleMessage(
            stranger, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(new byte[] { 1, 2, 3 })));
        await context.Service.LastWork;

        // Assert
        Assert.Empty(context.Service.GetRetrievals());
    }

    [Fact]
    public async Task Given_ARestartWithFewerChannels_When_ThePeersCopyNamesALostChannel_Then_NoBackupReplacesIt()
    {
        // Arrange: the peer keeps our backup naming two channels; we restarted from an older database that lost one
        using var context = new PeerStorageTestContext(options: new PeerStorageOptions
        {
            RetrievalWait = TimeSpan.FromSeconds(30)
        });
        var peer = new FakeGossipPeer(36);
        context.AddChannel(peer.PeerPubKey);
        var lost = context.AddChannel(new FakeGossipPeer(37).PeerPubKey);
        var keptByPeer = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        context.Channels.Remove(lost);
        context.KnownChannelIds.Remove(lost.ChannelId);

        // Act: first connection of the process, then the peer's retrieval, then the wait and a round pass
        context.Service.OnPeerInitialized(peer);
        var waiting = context.Service.LastWork;
        var nothingBeforeRetrieval = await peer.NothingSentWithinAsync(s_quiet);
        context.Service.HandleMessage(
            peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(keptByPeer!.Blob)));
        await context.Service.LastWork;
        await waiting;
        context.Time.Advance(TimeSpan.FromMinutes(1));
        await context.Service.RunRoundAsync();
        var another = new FakeGossipPeer(38);
        context.AddChannel(another.PeerPubKey);
        context.Service.OnPeerInitialized(another);
        context.Time.Advance(TimeSpan.FromSeconds(30));
        await context.Service.LastWork;

        // Assert: the data loss is recorded and our backup goes to nobody (the peer's copy is the evidence)
        Assert.True(nothingBeforeRetrieval);
        Assert.True(context.Service.BackupsHeldForDataLoss);
        var retrieval = Assert.Single(context.Service.GetRetrievals());
        Assert.Equal(lost.ChannelId, Assert.Single(retrieval.UnknownChannels).ChannelId);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        Assert.True(await another.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_ARestart_When_ThePeersRetrievalMatchesOurChannels_Then_TheBackupFollowsIt()
    {
        // Arrange
        using var context = new PeerStorageTestContext(options: new PeerStorageOptions
        {
            RetrievalWait = TimeSpan.FromSeconds(30)
        });
        var peer = new FakeGossipPeer(39);
        context.AddChannel(peer.PeerPubKey);
        var older = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        context.AddChannel(new FakeGossipPeer(40).PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);
        var waiting = context.Service.LastWork;
        context.Service.HandleMessage(
            peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(older!.Blob)));
        await context.Service.LastWork;
        await waiting;

        // Assert: no data loss (every channel it names is known), so the current backup replaces it at once
        Assert.False(context.Service.BackupsHeldForDataLoss);
        var sent = await peer.NextAsync<PeerStorageMessage>();
        var contents = await context.BlobProvider.TryReadBlobAsync(sent.Payload.Blob,
                                                                   TestContext.Current.CancellationToken);
        Assert.Equal(2, contents!.Channels.Count);
    }

    [Fact]
    public async Task Given_ARestart_When_ThePeerSendsNoRetrieval_Then_TheBackupIsSentAfterTheWait()
    {
        // Arrange
        using var context = new PeerStorageTestContext(options: new PeerStorageOptions
        {
            RetrievalWait = TimeSpan.FromSeconds(30)
        });
        var peer = new FakeGossipPeer(41);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);
        var waiting = context.Service.LastWork;
        var nothingBeforeTheWait = await peer.NothingSentWithinAsync(s_quiet);
        context.Time.Advance(TimeSpan.FromSeconds(30));
        await waiting;

        // Assert
        Assert.True(nothingBeforeTheWait);
        Assert.IsType<PeerStorageMessage>(await peer.NextAsync<PeerStorageMessage>());
    }

    [Fact]
    public async Task Given_ARetrievalBeforeTheInitHook_When_ThePeerLostOurBackup_Then_TheNextConnectionSendsIt()
    {
        // Arrange: we sent our backup; the peer lost it and, on its next connection, its retrieval (an older backup
        // of ours) is handled before our init hook registered that connection
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(42);
        context.AddChannel(peer.PeerPubKey);
        var older = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        context.AddChannel(new FakeGossipPeer(43).PeerPubKey);
        context.Service.OnPeerInitialized(peer);
        await peer.NextAsync<PeerStorageMessage>();
        peer.Disconnect();
        var reconnected = new FakeGossipPeer(42);

        // Act
        context.Service.HandleMessage(
            reconnected, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(older!.Blob)));
        await context.Service.LastWork;
        context.Service.OnPeerInitialized(reconnected);
        await context.Service.LastWork;

        // Assert
        var resent = await reconnected.NextAsync<PeerStorageMessage>();
        var contents = await context.BlobProvider.TryReadBlobAsync(resent.Payload.Blob,
                                                                   TestContext.Current.CancellationToken);
        Assert.Equal(2, contents!.Channels.Count);
    }

    #endregion
}