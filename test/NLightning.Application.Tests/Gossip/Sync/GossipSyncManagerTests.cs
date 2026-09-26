using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Sync;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Queries;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// BOLT 7 plan G3-T2: the sync state machine against a fake peer (plus the query dispatch of G3-T1 and NL-353).
/// </summary>
public class GossipSyncManagerTests : IDisposable
{
    private const uint Tip = 500;
    private static readonly ChainHash s_chain = ChainConstants.Regtest;
    private static readonly TimeSpan s_quiet = TimeSpan.FromMilliseconds(200);

    private readonly SyncTestGraph _graph = new();
    private readonly Mock<IGossipIngress> _ingress = new();
    private readonly List<GossipSyncManager> _managers = [];
    private List<ShortChannelId> _missed = [];

    public GossipSyncManagerTests()
    {
        _ingress.SetupGet(i => i.IsEnabled).Returns(true);
    }

    private uint Now => (uint)_graph.Kit.Clock.GetUtcNow().ToUnixTimeSeconds();

    [Fact]
    public async Task Given_AGossipQueriesPeer_When_Initialized_Then_RangeQueryDiffScidQueryThenBacklogFilter()
    {
        // Arrange
        var known = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1);

        // Act / Assert: plan §3.7 steps 1-5
        manager.OnPeerInitialized(peer);
        var rangeQuery = await peer.NextAsync<QueryChannelRangeMessage>();
        Assert.Equal(s_chain, rangeQuery.Payload.ChainHash);
        Assert.Equal(0u, rangeQuery.Payload.FirstBlocknum);
        Assert.Equal(Tip + 1, rangeQuery.Payload.NumberOfBlocks);
        Assert.Null(rangeQuery.QueryOptionTlv);

        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, 300, false, known.ShortChannelId,
                                                                   new ShortChannelId(200, 1, 0)));
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(300, 201, true, new ShortChannelId(300, 2, 1)));
        var scidQuery = await peer.NextAsync<QueryShortChannelIdsMessage>();
        Assert.Equal([new ShortChannelId(200, 1, 0), new ShortChannelId(300, 2, 1)], Ids(scidQuery));
        Assert.Null(scidQuery.QueryFlagsTlv);
        Assert.False(manager.HasCompletedInitialSync);

        manager.HandleMessage(peer, End());
        var filter = await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(Now - 1_209_600, filter.Payload.FirstTimestamp);
        Assert.Equal(uint.MaxValue, filter.Payload.TimestampRange);
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.True(manager.HasCompletedInitialSync);
        Assert.Empty(peer.Warnings);
    }

    [Fact]
    public async Task Given_EveryChannelKnown_When_TheRangeSyncEnds_Then_NoScidQueryOnlyTheFilter()
    {
        // Arrange
        var known = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, known.ShortChannelId));

        // Assert
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.True(manager.HasCompletedInitialSync);
    }

    [Theory]
    [InlineData("starts late")]
    [InlineData("short final")]
    [InlineData("decreasing")]
    [InlineData("zlib")]
    [InlineData("other chain")]
    public async Task Given_ABadRangeReply_When_Received_Then_AWarningAndTheSyncEnds(string problem)
    {
        // Arrange
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        switch (problem)
        {
            case "starts late":
                manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(10, 600, true));
                break;
            case "short final":
                manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, 100, true));
                break;
            case "decreasing":
                manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, 200, false));
                manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(200, 100, false));
                manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(100, 600, true));
                break;
            case "zlib":
                manager.HandleMessage(peer, new ReplyChannelRangeMessage(
                                          new ReplyChannelRangePayload(s_chain, 0, 600, true,
                                                                       new byte[] { 1, 0x78, 0x9c })));
                break;
            case "other chain":
                manager.HandleMessage(peer, new ReplyChannelRangeMessage(
                                          new ReplyChannelRangePayload(ChainConstants.Main, 0, 600, true,
                                                                       new byte[] { 0 })));
                break;
        }

        // Assert: B7-Q-04 violation → warning, no scid query, no filter, no full_information
        await peer.NextAsync<WarningMessage>();
        Assert.Single(peer.Warnings);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.False(manager.HasCompletedInitialSync);
    }

    [Fact]
    public async Task Given_ManyUnknownChannels_When_Synced_Then_OneScidQueryIsOutstandingAtATime()
    {
        // Arrange: B7-Q-01, batches of 2
        var manager = CreateManager(o => o.MaxScidsPerQuery = 2);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        var ids = Enumerable.Range(0, 5).Select(i => new ShortChannelId(100 + (uint)i, 0, 0)).ToArray();

        // Act / Assert
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        Assert.Equal(ids[..2], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        manager.HandleMessage(peer, End());
        Assert.Equal(ids[2..4], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        manager.HandleMessage(peer, End());
        Assert.Equal(ids[4..], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peer, End());
        await peer.NextAsync<GossipTimestampFilterMessage>();
    }

    [Fact]
    public async Task Given_GossipQueriesExOnBothSides_When_Synced_Then_TimestampsDecideTheQueryFlags()
    {
        // Arrange: G3-T4, a known channel with a newer direction-2 update at the peer, one up to date, one unknown
        var newer = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                                            1_700_000_000, 1_700_000_001);
        var same = _graph.AddSignedChannel(new ShortChannelId(101, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC,
                                           1_700_000_000, 1_700_000_001);
        var unknown = new ShortChannelId(102, 0, 0);
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1, gossipQueriesEx: true);
        manager.OnPeerInitialized(peer);

        // Act
        var rangeQuery = await peer.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(peer, new ReplyChannelRangeMessage(
                                  new ReplyChannelRangePayload(
                                      s_chain, 0, Tip + 1, true,
                                      GossipQueryCodec.EncodeShortChannelIds(
                                          [newer.ShortChannelId, same.ShortChannelId, unknown])),
                                  new BaseTlv(TlvConstants.ReplyChannelRangeTimestamps,
                                              GossipQueryCodec.EncodeTimestamps(
                                                  [new(1_700_000_000, 1_700_000_050), new(1_700_000_000, 1_700_000_001),
                                                   new(1_700_000_100, 0)]))));
        var scidQuery = await peer.NextAsync<QueryShortChannelIdsMessage>();

        // Assert
        Assert.NotNull(rangeQuery.QueryOptionTlv);
        Assert.Equal(GossipQueryCodec.QueryOptionTimestamps,
                     GossipQueryCodec.DecodeQueryOption(rangeQuery.QueryOptionTlv.Value));
        Assert.Equal([newer.ShortChannelId, unknown], Ids(scidQuery));
        Assert.NotNull(scidQuery.QueryFlagsTlv);
        Assert.Equal([GossipQueryCodec.QueryFlagChannelUpdate2, GossipQueryCodec.QueryFlagAll],
                     GossipQueryCodec.DecodeQueryFlags(scidQuery.QueryFlagsTlv.Value, 2));
    }

    [Fact]
    public async Task Given_APeerWithoutGossipQueries_When_Initialized_Then_OnlyTheNothingFilter()
    {
        // Arrange: B7-Q-06
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1, gossipQueries: false);

        // Act
        manager.OnPeerInitialized(peer);

        // Assert
        var filter = await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(uint.MaxValue, filter.Payload.FirstTimestamp);
        Assert.Equal(0u, filter.Payload.TimestampRange);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_EnoughSyncPeers_When_AnotherGossipPeerConnects_Then_ItOnlyGetsAFilterForNewGossip()
    {
        // Arrange
        var manager = CreateManager(o => o.SyncPeers = 1);
        var first = new FakeGossipPeer(1);
        var second = new FakeGossipPeer(2);

        // Act
        manager.OnPeerInitialized(first);
        manager.OnPeerInitialized(second);

        // Assert
        await first.NextAsync<QueryChannelRangeMessage>();
        var filter = await second.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(Now, filter.Payload.FirstTimestamp);
        Assert.Equal(uint.MaxValue, filter.Payload.TimestampRange);
    }

    [Fact]
    public async Task Given_ASyncPeerDisconnects_When_AnotherConnects_Then_TheSlotIsFree()
    {
        // Arrange
        var manager = CreateManager(o => o.SyncPeers = 1);
        var first = new FakeGossipPeer(1);
        manager.OnPeerInitialized(first);
        await first.NextAsync<QueryChannelRangeMessage>();

        // Act
        first.Disconnect();
        var second = new FakeGossipPeer(2);
        manager.OnPeerInitialized(second);

        // Assert
        await second.NextAsync<QueryChannelRangeMessage>();
    }

    [Fact]
    public async Task Given_TheSyncIsOff_When_APeerConnects_Then_NothingIsSentButQueriesAreAnswered()
    {
        // Arrange: plan D12, the graph off (mainnet default)
        _ingress.SetupGet(i => i.IsEnabled).Returns(false);
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1);

        // Act
        manager.OnPeerInitialized(peer);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        manager.HandleMessage(peer, new QueryChannelRangeMessage(new QueryChannelRangePayload(s_chain, 0, 10)));

        // Assert
        var reply = await peer.NextAsync<ReplyChannelRangeMessage>();
        Assert.True(reply.Payload.SyncComplete);
    }

    [Fact]
    public async Task Given_ANonAnsweringPeer_When_TheReplyTimeoutPasses_Then_TheSyncEndsWithoutAWarning()
    {
        // Arrange
        var manager = CreateManager(o => o.SyncReplyTimeout = TimeSpan.FromMilliseconds(150));
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true)); // too late: unsolicited

        // Assert
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        Assert.Empty(peer.Warnings);
        Assert.False(manager.HasCompletedInitialSync);
    }

    [Fact]
    public async Task Given_PeerQueries_When_Received_Then_TheyAreAnsweredInOrderAndAnExcessQueryIsWarned()
    {
        // Arrange: queries arriving before init wait; one may wait, a second gets a warning (B7-Q-02 MAY)
        var channel = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        _ingress.SetupGet(i => i.IsEnabled).Returns(false);
        var manager = CreateManager(o => o.MaxQueuedQueriesPerPeer = 1);
        var peer = new FakeGossipPeer(1);
        manager.HandleMessage(peer, new QueryShortChannelIdsMessage(
                                  new QueryShortChannelIdsPayload(
                                      s_chain, GossipQueryCodec.EncodeShortChannelIds([channel.ShortChannelId]))));
        manager.HandleMessage(peer, new QueryChannelRangeMessage(new QueryChannelRangePayload(s_chain, 0, 10)));

        // Act
        manager.OnPeerInitialized(peer);

        // Assert: the warning went first (sent from the read loop), then the answer of the first query
        await peer.NextAsync<WarningMessage>();
        await peer.NextAsync<ChannelAnnouncementMessage>();
        await peer.NextAsync<ChannelUpdateMessage>();
        await peer.NextAsync<ChannelUpdateMessage>();
        var end = await peer.NextAsync<ReplyShortChannelIdsEndMessage>();
        Assert.False(end.Payload.FullInformation); // no initial sync yet
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_AMalformedPeerQuery_When_Answered_Then_AWarningAndTheNextQueryIsStillAnswered()
    {
        // Arrange
        _ingress.SetupGet(i => i.IsEnabled).Returns(false);
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);

        // Act
        manager.HandleMessage(peer, new QueryShortChannelIdsMessage(
                                  new QueryShortChannelIdsPayload(s_chain, new byte[] { 1, 0x78 })));
        manager.HandleMessage(peer, new QueryChannelRangeMessage(new QueryChannelRangePayload(s_chain, 0, 10)));

        // Assert
        await peer.NextAsync<WarningMessage>();
        await peer.NextAsync<ReplyChannelRangeMessage>();
    }

    [Fact]
    public async Task Given_AFullSync_When_APeerQueries_Then_FullInformationIsSet()
    {
        // Arrange
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true));
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);

        // Act
        manager.HandleMessage(peer, new QueryShortChannelIdsMessage(
                                  new QueryShortChannelIdsPayload(s_chain, new byte[] { 0 })));

        // Assert
        Assert.True((await peer.NextAsync<ReplyShortChannelIdsEndMessage>()).Payload.FullInformation);
    }

    [Fact]
    public async Task Given_APeerFilter_When_Received_Then_ItIsKeptAndRaisedAndAnotherChainsIsIgnored()
    {
        // Arrange
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1, gossipQueries: false);
        manager.OnPeerInitialized(peer);
        GossipFilterReceivedEventArgs? raised = null;
        manager.FilterReceived += (_, e) => raised = e;
        Assert.False(manager.TryGetPeerFilter(peer, out _));

        // Act
        manager.HandleMessage(peer, new GossipTimestampFilterMessage(
                                  new GossipTimestampFilterPayload(ChainConstants.Main, 5, 6)));
        var ignored = manager.TryGetPeerFilter(peer, out _);
        manager.HandleMessage(peer, new GossipTimestampFilterMessage(
                                  new GossipTimestampFilterPayload(s_chain, 1_000, 60)));

        // Assert
        Assert.False(ignored);
        Assert.True(manager.TryGetPeerFilter(peer, out var filter));
        Assert.Equal(new GossipTimestampFilter(1_000, 60), filter);
        Assert.NotNull(raised);
        Assert.Same(peer, raised.Peer);
        peer.Disconnect();
        Assert.False(manager.TryGetPeerFilter(peer, out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Given_QueryScid_When_APeerAnswers_Then_TrueAndOnlyThatChannelWasAsked()
    {
        // Arrange
        var manager = CreateManager(o => o.SyncPeers = 0);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();
        var scid = new ShortChannelId(123, 4, 5);

        // Act
        var answered = manager.QueryScidAsync(scid, TestContext.Current.CancellationToken);
        var query = await peer.NextAsync<QueryShortChannelIdsMessage>();
        manager.HandleMessage(peer, End());

        // Assert
        Assert.Equal([scid], Ids(query));
        Assert.True(await answered);
    }

    [Fact]
    public async Task Given_NoPeerOrASpentChannelOrADisconnect_When_QueryScid_Then_False()
    {
        // Arrange
        var spent = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                                            spentAtHeight: 200);
        var manager = CreateManager(o => o.SyncPeers = 0);
        var ct = TestContext.Current.CancellationToken;

        // Act / Assert
        Assert.False(await manager.QueryScidAsync(new ShortChannelId(101, 0, 0), ct));
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        Assert.False(await manager.QueryScidAsync(spent.ShortChannelId, ct)); // B7-Q-01
        var waiting = manager.QueryScidAsync(new ShortChannelId(101, 0, 0), ct);
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await peer.NextAsync<QueryShortChannelIdsMessage>();
        peer.Disconnect();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task Given_ChannelsTheIngressDropped_When_Retried_Then_TheMissingOnesAreQueriedAgain()
    {
        // Arrange: NL-353
        var known = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var dropped = new[] { new ShortChannelId(300, 0, 0), new ShortChannelId(200, 1, 0) };
        _missed = [known.ShortChannelId, .. dropped];
        var manager = CreateManager(o => o.SyncPeers = 0);

        // Act / Assert: no peer yet, the ids are kept
        Assert.Equal(0, manager.RetryMissedShortChannelIds());
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(2, manager.RetryMissedShortChannelIds());
        Assert.Equal([dropped[1], dropped[0]], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peer, End());
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.Equal(0, manager.RetryMissedShortChannelIds());
    }

    [Fact]
    public async Task Given_TwoPeers_When_TheSyncRotates_Then_ThePeerSyncedLongestAgoRunsTheRangeQuery()
    {
        // Arrange
        var manager = CreateManager(o => o.SyncPeers = 1);
        var first = new FakeGossipPeer(1);
        var second = new FakeGossipPeer(2);
        manager.OnPeerInitialized(first);
        await first.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(first, RangeReplyCollectorTests.Reply(0, Tip + 1, true));
        await first.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(first, TestContext.Current.CancellationToken);
        manager.OnPeerInitialized(second);
        await second.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(second, TestContext.Current.CancellationToken);

        // Act
        var rotated = manager.RotateSyncPeer();

        // Assert
        Assert.Same(second, rotated);
        await second.NextAsync<QueryChannelRangeMessage>();
        Assert.True(await first.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_AnUnsolicitedReply_When_Received_Then_ItIsIgnored()
    {
        // Arrange
        var manager = CreateManager(o => o.SyncPeers = 0);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();

        // Act
        manager.HandleMessage(peer, End());
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, 10, true));

        // Assert
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        Assert.Empty(peer.Warnings);
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.Dispose();
    }

    private GossipSyncManager CreateManager(Action<GossipSyncOptions>? configure = null)
    {
        var options = new GossipSyncOptions();
        configure?.Invoke(options);
        var manager = new GossipSyncManager(_graph.Store, Microsoft.Extensions.Options.Options.Create(options),
                                            Microsoft.Extensions.Options.Options.Create(new NodeOptions
                                            {
                                                BitcoinNetwork = BitcoinNetwork.Resolve("regtest")
                                            }), NullLogger<GossipSyncManager>.Instance, _graph.Kit.Clock,
                                            _ingress.Object, () =>
                                            {
                                                var taken = _missed;
                                                _missed = [];
                                                return taken;
                                            }, () => Tip);
        _managers.Add(manager);
        return manager;
    }

    private static ReplyShortChannelIdsEndMessage End() => new(new ReplyShortChannelIdsEndPayload(s_chain, true));

    private static ShortChannelId[] Ids(QueryShortChannelIdsMessage query) =>
        GossipQueryCodec.DecodeShortChannelIds(query.Payload.EncodedShortIds.Span, "test");
}