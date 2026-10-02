using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
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

    /// <summary>The reply timeout of every timeout test (NL-501: fired on the stepped clock, not the wall clock).</summary>
    private static readonly TimeSpan s_replyTimeout = TimeSpan.FromMilliseconds(150);

    private readonly SyncTestGraph _graph = new();
    private readonly Mock<IGossipIngress> _ingress = new();
    private readonly List<GossipSyncManager> _managers = [];
    private readonly SteppedClockProvider _syncClock = new();
    private List<ShortChannelId> _missed = [];

    public GossipSyncManagerTests()
    {
        _ingress.SetupGet(i => i.IsEnabled).Returns(true);

        // Land the stepped clock at a half-second boundary: the filter assertions compare seconds read from this
        // clock at send time and at assert time, so half a second of margin keeps them in the same second
        var millis = _syncClock.GetUtcNow().TimeOfDay.TotalMilliseconds;
        _syncClock.Advance(TimeSpan.FromMilliseconds(500 - millis % 500));
    }

    private uint Now => (uint)_syncClock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>
    /// Advances the stepped sync clock past <paramref name="by"/>, landing at a half-second boundary again so a
    /// <see cref="Now"/> read cannot tick over between the manager's send and the assertion.
    /// </summary>
    private void AdvancePast(TimeSpan by)
    {
        var millis = _syncClock.GetUtcNow().TimeOfDay.TotalMilliseconds;
        _syncClock.Advance(TimeSpan.FromMilliseconds(Math.Ceiling((millis + by.TotalMilliseconds) / 500.0) * 500
                                                     - millis));
    }

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

        // Assert: B7-Q-04 violation → warning, no scid query, no full_information; only new gossip is asked for, and
        // nothing is queried on that connection again
        await peer.NextAsync<WarningMessage>();
        Assert.Single(peer.Warnings);
        var filter = await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(Now, filter.Payload.FirstTimestamp);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.False(manager.HasCompletedInitialSync);
        Assert.Null(manager.RotateSyncPeer());
        Assert.False(await manager.QueryScidAsync(new ShortChannelId(101, 0, 0), TestContext.Current.CancellationToken));
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
    public async Task Given_ChannelsLearnedDuringTheSync_When_TheNextBatchGoesOut_Then_TheyAreNotAskedForAgain()
    {
        // Arrange: NL-402, batches of 2; while the first batch is out, another sync peer delivers ids[2] and ids[3]
        var manager = CreateManager(o => o.MaxScidsPerQuery = 2);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        var ids = Enumerable.Range(0, 5).Select(i => new ShortChannelId(100 + (uint)i, 0, 0)).ToArray();
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        Assert.Equal(ids[..2], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));

        // Act
        _graph.AddSignedChannel(ids[2], SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        _graph.AddSignedChannel(ids[3], SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        manager.HandleMessage(peer, End());

        // Assert: only ids[4] is left to ask for, then the sync ends as usual
        Assert.Equal(ids[4..], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peer, End());
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.True(manager.HasCompletedInitialSync);
    }

    [Fact]
    public async Task Given_EveryRemainingChannelLearnedDuringTheSync_When_TheBatchEnds_Then_TheSyncCompletesWithoutAnotherQuery()
    {
        // Arrange
        var manager = CreateManager(o => o.MaxScidsPerQuery = 2);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        var ids = Enumerable.Range(0, 4).Select(i => new ShortChannelId(100 + (uint)i, 0, 0)).ToArray();
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        await peer.NextAsync<QueryShortChannelIdsMessage>();

        // Act
        _graph.AddSignedChannel(ids[2], SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        _graph.AddSignedChannel(ids[3], SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        manager.HandleMessage(peer, End());

        // Assert: straight to the filter
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        Assert.True(manager.HasCompletedInitialSync);
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
                                                   new(Now - 60, 0)]))));
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

    [Theory]
    [InlineData(0, false)] // the option off: every unknown channel is asked for
    [InlineData(1_209_600, true)]
    public async Task Given_UnknownChannelsWhoseUpdatesThePeerReportsStale_When_Synced_Then_OnlyFreshOnesAreAsked(
        int skipStaleSeconds, bool skipped)
    {
        // Arrange: NL-404, an unknown channel with a fresh update, one with both updates older than two weeks, one
        // with none (both timestamps 0)
        var fresh = new ShortChannelId(100, 0, 0);
        var stale = new ShortChannelId(101, 0, 0);
        var withoutUpdates = new ShortChannelId(102, 0, 0);
        var manager = CreateManager(o => o.SkipChannelsStaleFor = TimeSpan.FromSeconds(skipStaleSeconds));
        var peer = new FakeGossipPeer(1, gossipQueriesEx: true);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peer, new ReplyChannelRangeMessage(
                                  new ReplyChannelRangePayload(
                                      s_chain, 0, Tip + 1, true,
                                      GossipQueryCodec.EncodeShortChannelIds([fresh, stale, withoutUpdates])),
                                  new BaseTlv(TlvConstants.ReplyChannelRangeTimestamps,
                                              GossipQueryCodec.EncodeTimestamps(
                                                  [new(Now - 1_209_000, 0), new(Now - 1_210_000, Now - 1_300_000),
                                                   new(0, 0)]))));
        var scidQuery = await peer.NextAsync<QueryShortChannelIdsMessage>();

        // Assert
        Assert.Equal(skipped ? [fresh] : [fresh, stale, withoutUpdates], Ids(scidQuery));
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
    public async Task Given_AChannelPeerAtSyncPeerCapacity_When_ItConnects_Then_ItTakesTheStrangersSlot()
    {
        // Arrange (NL-363: sync peers are channel peers first): peer 2 is the only one we have a channel with
        var stranger = new FakeGossipPeer(1);
        var channelPeer = new FakeGossipPeer(2);
        var manager = CreateManager(o => o.SyncPeers = 1, hasChannelWith: peer => peer == channelPeer.PeerPubKey);
        manager.OnPeerInitialized(stranger);
        await stranger.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.OnPeerInitialized(channelPeer);

        // Assert: the channel peer runs the range query, the stranger keeps only its live filter
        await channelPeer.NextAsync<QueryChannelRangeMessage>();
        Assert.True(await stranger.NothingSentWithinAsync(s_quiet));
    }

    [Fact]
    public async Task Given_ASyncPeerWhoseQueryTimedOut_When_ANewPeerConnects_Then_TheNewPeerTakesTheSlot()
    {
        // Arrange (NL-363: prefer peers that answer): the first sync peer never answered its range query
        var manager = CreateManager(o =>
        {
            o.SyncPeers = 1;
            o.SyncReplyTimeout = s_replyTimeout;
        });
        var slow = new FakeGossipPeer(1);
        manager.OnPeerInitialized(slow);
        await slow.NextAsync<QueryChannelRangeMessage>();

        // Act (NL-501): the reply timeout fires on the stepped clock
        AdvancePast(s_replyTimeout);
        await IdleAsync(manager, slow);
        var fresh = new FakeGossipPeer(2);
        manager.OnPeerInitialized(fresh);

        // Assert: the fresh peer (no failed queries) takes the sync slot and runs the range query
        await fresh.NextAsync<QueryChannelRangeMessage>();
        await slow.NextAsync<GossipTimestampFilterMessage>(); // the failed sync peer's live filter
        Assert.True(await slow.NothingSentWithinAsync(s_quiet));
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
        var manager = CreateManager(o => o.SyncReplyTimeout = s_replyTimeout);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act (NL-501): the reply timeout fires on the stepped clock
        AdvancePast(s_replyTimeout);
        await IdleAsync(manager, peer);
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true)); // too late: unsolicited

        // Assert: the failed sync peer still asks for new gossip (LND/CLN relay nothing before a filter)
        var filter = await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(Now, filter.Payload.FirstTimestamp);
        Assert.Equal(uint.MaxValue, filter.Payload.TimestampRange);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        Assert.Empty(peer.Warnings);
        Assert.False(manager.HasCompletedInitialSync);
    }

    [Fact]
    public async Task Given_APeerThatIgnoresScidQueries_When_TheScidQueryTimesOut_Then_TheBacklogFilterAsksForItsGossip()
    {
        // Arrange (NL-722): an LDK peer answers query_channel_range but never query_short_channel_ids (rust-lightning
        // 0.3 leaves it unimplemented); LDK streams its graph only to a filter that starts more than 6 h ago
        var manager = CreateManager(o => o.SyncReplyTimeout = s_replyTimeout);
        var peer = new FakeGossipPeer(1);
        var startedAt = Now;
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, new ShortChannelId(200, 1, 0)));
        Assert.Equal([new ShortChannelId(200, 1, 0)], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));

        // Act: the scid query's reply timeout fires on the stepped clock
        AdvancePast(s_replyTimeout);

        // Assert: the backlog filter of a sync without timestamps, not a live filter from now
        var filter = await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(startedAt - 1_209_600, filter.Payload.FirstTimestamp);
        Assert.Equal(uint.MaxValue, filter.Payload.TimestampRange);
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
        Assert.Empty(peer.Warnings);
        Assert.False(manager.HasCompletedInitialSync);
    }

    [Fact]
    public async Task Given_ATimedOutScidQuery_When_ItsLateEndArrives_Then_TheNextQueryWaitsForItAndRuns()
    {
        // Arrange (NL-365; BOLT 7: MUST NOT send query_short_channel_ids before the previous one's
        // reply_short_channel_ids_end): the peer answers the scid query after our timeout
        var manager = CreateManager(o =>
        {
            o.SyncReplyTimeout = s_replyTimeout;
            o.SyncPeers = 0;
        });
        var slow = new FakeGossipPeer(1);
        manager.OnPeerInitialized(slow);
        await slow.NextAsync<GossipTimestampFilterMessage>();
        var first = manager.QueryScidAsync(new ShortChannelId(200, 1, 0), TestContext.Current.CancellationToken);
        Assert.Equal([new ShortChannelId(200, 1, 0)], Ids(await slow.NextAsync<QueryShortChannelIdsMessage>()));

        // The timeout fires on the stepped clock (NL-501): nothing was asked again meanwhile
        AdvancePast(s_replyTimeout);
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Act: the next query waits for the outstanding end; nothing goes out until it arrives
        var waiting = manager.QueryScidAsync(new ShortChannelId(300, 1, 0), TestContext.Current.CancellationToken);
        Assert.True(await slow.NothingSentWithinAsync(s_quiet));
        manager.HandleMessage(slow, End()); // the late reply_short_channel_ids_end

        // Assert: the query goes out now and completes when its own end arrives
        await slow.NextAsync<QueryShortChannelIdsMessage>();
        Assert.False(waiting.IsCompleted);
        manager.HandleMessage(slow, End());
        Assert.True(await waiting);
        Assert.Empty(slow.Warnings);
    }

    [Fact]
    public async Task Given_ATimedOutRangeQuery_When_ItsLateRepliesComplete_Then_TheConnectionQueriesAgain()
    {
        // Arrange (NL-365): the peer answers the range query after our timeout
        var manager = CreateManager(o =>
        {
            o.SyncReplyTimeout = s_replyTimeout;
            o.SyncPeers = 1;
        });
        var slow = new FakeGossipPeer(1);
        manager.OnPeerInitialized(slow);
        await slow.NextAsync<QueryChannelRangeMessage>();

        // The timeout fires on the stepped clock (NL-501): the live filter follows the abandoned wait
        AdvancePast(s_replyTimeout);
        await slow.NextAsync<GossipTimestampFilterMessage>();
        await IdleAsync(manager, slow);

        // Act: the rotation asks the same connection again; its querier first consumes the late replies, which
        // complete the abandoned collector, then runs the new range query
        Assert.Same(slow, manager.RotateSyncPeer());
        manager.HandleMessage(slow, RangeReplyCollectorTests.Reply(0, 300, false, new ShortChannelId(200, 1, 0)));
        manager.HandleMessage(slow, RangeReplyCollectorTests.Reply(300, Tip + 1 - 300, true));
        await slow.NextAsync<QueryChannelRangeMessage>();

        // Assert: the new sync runs normally on the recovered connection
        manager.HandleMessage(slow, RangeReplyCollectorTests.Reply(0, Tip + 1, true, new ShortChannelId(200, 1, 0)));
        Assert.Equal([new ShortChannelId(200, 1, 0)], Ids(await slow.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(slow, End());
        await slow.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(slow, TestContext.Current.CancellationToken);
        Assert.True(manager.HasCompletedInitialSync);
        Assert.Empty(slow.Warnings);
    }

    [Fact]
    public async Task Given_AnIngressQueue_When_Syncing_Then_BatchesFitItAndWaitForItToDrain()
    {
        // Arrange (NL-353, review of G3-T2): capacity 100 → at most 100 / 2 / 5 = 10 channels per query, sent only
        // while the queue is at most 50
        var depth = 60;
        var manager = CreateManager(getIngressQueueDepth: () => Volatile.Read(ref depth), ingressQueueCapacity: 100);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        var ids = Enumerable.Range(0, 15).Select(i => new ShortChannelId(100 + (uint)i, 0, 0)).ToArray();

        // Act / Assert: nothing goes out while the queue is full (the querier waits on the stepped clock); the
        // query goes out once the queue has drained to half the capacity
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        Assert.True(await peer.NothingSentWithinAsync(TimeSpan.FromMilliseconds(400)));
        Volatile.Write(ref depth, 50);
        _syncClock.Advance(TimeSpan.FromMilliseconds(150)); // the ingress poll wakes
        Assert.Equal(ids[..10], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        Volatile.Write(ref depth, 51);
        manager.HandleMessage(peer, End());
        Assert.True(await peer.NothingSentWithinAsync(TimeSpan.FromMilliseconds(400)));
        Volatile.Write(ref depth, 0);
        _syncClock.Advance(TimeSpan.FromMilliseconds(150)); // the ingress poll wakes
        Assert.Equal(ids[10..], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peer, End());
        await peer.NextAsync<GossipTimestampFilterMessage>();
    }

    [Fact]
    public async Task Given_PerPeerQueueDepths_When_Syncing_Then_OnlyThePeersOwnQueueHoldsTheQueryBackWithoutTimeLimit()
    {
        // Arrange (NL-412): capacity 100 → queries of 10 channels while the peer's own queue is at most 50; the whole
        // queue (other peers' gossip) is far fuller, and the peer's own queue stays full past the reply timeout
        var ownDepth = 0;
        var manager = CreateManager(o => o.SyncReplyTimeout = s_replyTimeout,
                                    getIngressQueueDepth: () => 10_000, ingressQueueCapacity: 100,
                                    getPeerQueueDepth: _ => Volatile.Read(ref ownDepth));
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        var ids = Enumerable.Range(0, 15).Select(i => new ShortChannelId(100 + (uint)i, 0, 0)).ToArray();

        // Act / Assert: the first query goes out at once although the whole queue is full
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        Assert.Equal(ids[..10], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));

        // Its answer fills the peer's own queue: the next query waits on the stepped clock, also past the reply
        // timeout
        Volatile.Write(ref ownDepth, 51);
        manager.HandleMessage(peer, End());
        Assert.True(await peer.NothingSentWithinAsync(TimeSpan.FromMilliseconds(600)));
        _syncClock.Advance(TimeSpan.FromMilliseconds(300)); // past the reply timeout: still waiting
        Volatile.Write(ref ownDepth, 50);
        _syncClock.Advance(TimeSpan.FromMilliseconds(150)); // the ingress poll wakes
        Assert.Equal(ids[10..], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peer, End());
        await peer.NextAsync<GossipTimestampFilterMessage>();
    }

    [Fact]
    public async Task Given_ASyncWithTimestamps_When_ItEnds_Then_TheFilterStartsAtTheSyncLessTheMargin()
    {
        // Arrange (review of G3-T2: the two-week backlog would ask for the whole graph again; with timestamps the
        // sync already asked for every newer update)
        var manager = CreateManager();
        var peer = new FakeGossipPeer(1, gossipQueriesEx: true);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true));

        // Assert
        var filter = await peer.NextAsync<GossipTimestampFilterMessage>();
        Assert.Equal(Now - GossipSyncManager.TimestampSyncFilterMarginSeconds, filter.Payload.FirstTimestamp);
        Assert.Equal(uint.MaxValue, filter.Payload.TimestampRange);
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
    public async Task Given_ADroppedUpdateOfAStoredChannel_When_Retried_Then_ItIsQueriedAgainUntilBothPoliciesAreKnown()
    {
        // Arrange: NL-410, a channel_update dropped at a full queue while its announcement was stored
        var complete = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA,
                                               SyncTestGraph.NodeB);
        var oneSided = _graph.AddSignedChannel(new ShortChannelId(200, 0, 0), SyncTestGraph.NodeA,
                                               SyncTestGraph.NodeC, timestamp2: null);
        var withoutPolicy = _graph.AddSignedChannel(new ShortChannelId(300, 0, 0), SyncTestGraph.NodeB,
                                                    SyncTestGraph.NodeC, null, null);
        var spent = _graph.AddSignedChannel(new ShortChannelId(400, 0, 0), SyncTestGraph.NodeB, SyncTestGraph.NodeC,
                                            timestamp2: null, spentAtHeight: 450);
        _missed =
        [
            complete.ShortChannelId, oneSided.ShortChannelId, withoutPolicy.ShortChannelId, spent.ShortChannelId
        ];
        var manager = CreateManager(o => o.SyncPeers = 0);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();

        // Act
        var queued = manager.RetryMissedShortChannelIds();

        // Assert: the channels missing a policy are asked for again, once
        Assert.Equal(2, queued);
        Assert.Equal([oneSided.ShortChannelId, withoutPolicy.ShortChannelId],
                     Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
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

    [Fact]
    public async Task Given_ARegisteredSender_When_TheSyncRuns_Then_EverySyncMessageGoesThroughTheOutbox()
    {
        // Arrange: NL-361, our queries, replies and filters take the relay's outbox port, not the direct send
        var known = _graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var sender = new FakeGossipSender();
        var manager = CreateManager(peerSender: sender);
        var peer = new FakeGossipPeer(1);

        // Act: a whole range sync (query, scid query, filter) and one reply of ours
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, known.ShortChannelId,
                                                                   new ShortChannelId(200, 1, 0)));
        await peer.NextAsync<QueryShortChannelIdsMessage>();
        manager.HandleMessage(peer, End());
        await peer.NextAsync<GossipTimestampFilterMessage>();
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);
        var answered = manager.QueryScidAsync(new ShortChannelId(123, 4, 5),
                                              TestContext.Current.CancellationToken);
        await peer.NextAsync<QueryShortChannelIdsMessage>();
        manager.HandleMessage(peer, End());
        Assert.True(await answered);

        // Assert: everything went out as outbox offers for this connection, and the peer got exactly those
        Assert.Equal(peer.Sent.Count, sender.Offers.Count);
        Assert.All(sender.Offers, offer => Assert.Equal(new GossipPeer(peer.PeerPubKey, peer), offer.Peer));
        Assert.Equal(3, sender.Offers.Count(m => m.Message is QueryChannelRangeMessage
                                                                     or QueryShortChannelIdsMessage));
        Assert.Single(sender.Offers, m => m.Message is GossipTimestampFilterMessage);
    }

    [Fact]
    public async Task Given_AFullOutbox_When_AQueryIsOffered_Then_ItIsOfferedAgainUntilItFits()
    {
        // Arrange: NL-360, the outbox is at its gossip cap; a sync message is not dropped around it
        var sender = new FakeGossipSender();
        var manager = CreateManager(o => o.SyncPeers = 0, peerSender: sender);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();
        sender.ScriptNext(2, GossipEnqueueResult.Full);
        var answered = manager.QueryScidAsync(new ShortChannelId(123, 4, 5),
                                              TestContext.Current.CancellationToken);

        // Act / Assert: offered three times, delivered on the third (each retry waits on the stepped clock)
        await AdvanceUntilAsync(() => sender.OfferedCount == 4, "the query to fit the outbox");
        var query = await peer.NextAsync<QueryShortChannelIdsMessage>();
        manager.HandleMessage(peer, End());
        Assert.True(await answered);
        Assert.Equal([new ShortChannelId(123, 4, 5)],
                     GossipQueryCodec.DecodeShortChannelIds(query.Payload.EncodedShortIds.Span, "test"));
    }

    [Fact]
    public async Task Given_AConnectionNotInstalledYet_When_TheSyncStartsAndThePeerQueries_Then_BothGoOutOnceItIs()
    {
        // Arrange: NL-361, the peer service calls OnPeerInitialized (and hands over the peer's first query) before
        // the peer manager installs the connection, whose outbox answers Gone until then
        var sender = new FakeGossipSender();
        var manager = CreateManager(peerSender: sender);
        var peer = new FakeGossipPeer(1);
        sender.ScriptNext(6, GossipEnqueueResult.Gone);

        // Act
        manager.OnPeerInitialized(peer);
        manager.HandleMessage(peer, new QueryChannelRangeMessage(new QueryChannelRangePayload(s_chain, 0, 10)));

        // Assert: our range query and our reply to the peer's query both went out once the connection was current
        // (the Gone offers are retried on the stepped clock)
        await AdvanceUntilAsync(() => peer.Sent.Count >= 2, "the connection to be installed");
        var sent = peer.Sent;
        Assert.Single(sent, m => m is QueryChannelRangeMessage);
        Assert.Single(sent, m => m is ReplyChannelRangeMessage { Payload.SyncComplete: true });
        Assert.True(sender.OfferedCount >= 8);
    }

    [Fact]
    public async Task Given_AConnectionGone_When_ItDisconnects_Then_TheOfferedQueryFails()
    {
        // Arrange: a replaced (or never installed) connection is Gone and is disconnected by the peer manager
        var sender = new FakeGossipSender();
        var manager = CreateManager(o => o.SyncPeers = 0, peerSender: sender);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();
        sender.ScriptNext(int.MaxValue / 2, GossipEnqueueResult.Gone);
        var answered = manager.QueryScidAsync(new ShortChannelId(123, 4, 5), TestContext.Current.CancellationToken);
        await AdvanceUntilAsync(() => sender.OfferedCount >= 2, "the query to be offered again");

        // Act
        peer.Disconnect();

        // Assert: the query is given up and nothing reaches the peer
        Assert.False(await answered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(await peer.NothingSentWithinAsync(s_quiet));
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.Dispose();
    }

    private GossipSyncManager CreateManager(Action<GossipSyncOptions>? configure = null,
                                            Func<int>? getIngressQueueDepth = null, int ingressQueueCapacity = 0,
                                            Func<CompactPubKey, int>? getPeerQueueDepth = null,
                                            Func<CompactPubKey, bool>? hasChannelWith = null,
                                            FakeGossipSender? peerSender = null)
    {
        var options = new GossipSyncOptions();
        configure?.Invoke(options);
        var manager = new GossipSyncManager(_graph.Store, Microsoft.Extensions.Options.Options.Create(options),
                                            Microsoft.Extensions.Options.Options.Create(new NodeOptions
                                            {
                                                BitcoinNetwork = BitcoinNetwork.Resolve("regtest")
                                            }), NullLogger<GossipSyncManager>.Instance, _syncClock,
                                            _ingress.Object, () =>
                                            {
                                                var taken = _missed;
                                                _missed = [];
                                                return taken;
                                            }, () => Tip, getIngressQueueDepth, ingressQueueCapacity,
                                            getPeerQueueDepth: getPeerQueueDepth, hasChannelWith: hasChannelWith,
                                            peerSender: peerSender);
        _managers.Add(manager);
        return manager;
    }

    /// <summary>Waits, advancing the stepped clock so the manager's parked waits (ingress polls, outbox retries)
    /// proceed, until <paramref name="condition"/> holds.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, string description)
    {
        await WaitFor.TrueAsync(() =>
        {
            _syncClock.Advance(TimeSpan.FromMilliseconds(100));
            return condition();
        }, TimeSpan.FromSeconds(10), description, TestContext.Current.CancellationToken);
    }

    /// <summary>Bounded settle: after a stepped-clock advance the loops finish asynchronously.</summary>
    private async Task IdleAsync(GossipSyncManager manager, FakeGossipPeer peer) =>
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken)
                     .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static ReplyShortChannelIdsEndMessage End() => new(new ReplyShortChannelIdsEndPayload(s_chain, true));

    private static ShortChannelId[] Ids(QueryShortChannelIdsMessage query) =>
        GossipQueryCodec.DecodeShortChannelIds(query.Payload.EncodedShortIds.Span, "test");
}