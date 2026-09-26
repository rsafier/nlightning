using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Graph;
using Sync;

/// <summary>
/// BOLT 7 plan G3-T3: the relay of other nodes' gossip (B7-Q-05, B7-RL-01): no relay before a filter, the filter
/// boundaries, the 256 → 258 → 257 order, origin suppression, staggered flushes, the backlog of a new filter, and the
/// peer outbox as the send path (NL-351).
/// </summary>
public class GossipRelayOthersTests : IDisposable
{
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromSeconds(60);
    private static readonly ShortChannelId s_scid1 = new(500, 1, 0);
    private static readonly ShortChannelId s_scid2 = new(501, 1, 0);
    private static readonly ShortChannelId s_scid3 = new(502, 1, 0);
    private static readonly ShortChannelId s_scid4 = new(503, 1, 0);

    private readonly SyncTestGraph _graph = new();
    private readonly RelayTestClock _clock = new();
    private readonly List<GossipPeer> _peers = [];
    private readonly Dictionary<IPeerService, GossipTimestampFilter> _filters = new(ReferenceEqualityComparer.Instance);
    private readonly Mock<IGossipSyncManager> _syncManager = new();
    private readonly GossipOriginTracker _origins = new();
    private readonly GossipRelayScheduler _relay;

    public GossipRelayOthersTests() : this(new GossipRelayOptions())
    {
    }

    private GossipRelayOthersTests(GossipRelayOptions options, IGossipPeerSender? sender = null,
                                   ISecureKeyManager? keyManager = null)
    {
        options.RelayFlushInterval = s_flushInterval;
        options.RelayCollectInterval = TimeSpan.FromSeconds(10);
        options.RelayTickInterval = TimeSpan.FromSeconds(1);
        _syncManager.Setup(m => m.TryGetPeerFilter(It.IsAny<IPeerService>(), out It.Ref<GossipTimestampFilter>.IsAny))
                    .Returns(new TryGetFilter((IPeerService peer, out GossipTimestampFilter filter) =>
                                                  _filters.TryGetValue(peer, out filter)));
        var directory = new Mock<IGossipPeerDirectory>();
        directory.Setup(d => d.GetConnectedPeers()).Returns(() => _peers.ToList());
        _relay = new GossipRelayScheduler(directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                          Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                          Options.Create(new GossipOptions()), _clock, sender, _graph.Store,
                                          _syncManager.Object, _origins, Options.Create(options), keyManager);
    }

    private delegate bool TryGetFilter(IPeerService peer, out GossipTimestampFilter filter);

    [Fact]
    public async Task Given_APeerWithoutAFilter_When_GossipIsAccepted_Then_NothingOfOthersIsRelayedToIt()
    {
        // Arrange (B7-RL-01: MUST NOT relay gossip it did not generate before the peer's gossip_timestamp_filter)
        var peer = AddPeer(0x41, filter: null);
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public async Task Given_NewGossipInAnyOrder_When_Flushed_Then_AnnouncementsThenUpdatesThenNodesGoOut()
    {
        // Arrange (B7-Q-05: a channel_announcement before its channel_updates and the node_announcements)
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();
        _graph.AddNode(SyncTestGraph.NodeC, 1_700_000_005);
        _graph.AddSignedChannel(s_scid2, SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Equal([
            MessageTypes.ChannelAnnouncement, MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate,
            MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
            MessageTypes.NodeAnnouncement
        ], peer.Sent.Select(m => m.Type));
        var announcements = peer.Sent.OfType<ChannelAnnouncementMessage>().Select(m => m.Payload.ShortChannelId);
        Assert.Equal([s_scid1, s_scid2], announcements);
    }

    [Fact]
    public async Task Given_AnAnnouncementAndItsUpdateInTwoFlushWindows_When_Flushed_Then_TheAnnouncementGoesWithIt()
    {
        // Arrange (review of G3-T3: the 256 is stored before its 258 and a flush falls between them; a 256 alone is
        // never relayed, and a 258 without its 256 is ignored by the receiver)
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, null, null);
        await FlushAllAsync();
        Assert.Empty(peer.Sent);
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        ApplyUpdate(s_scid1, node1, 0, 1_700_000_000);
        await FlushAllAsync();

        // Assert
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate], peer.Sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Given_APeerWhoseSendStalls_When_TheRelayTicks_Then_TheOtherPeersStillGetTheirGossip()
    {
        // Arrange (review of G3-T3: a peer that stops reading its socket must not stop the relay to every peer)
        var sender = new StallingSender();
        using var relay = new GossipRelayOthersTests(
            new GossipRelayOptions { RelaySendWait = TimeSpan.FromMilliseconds(100) }, sender);
        var slow = relay.AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        var fast = relay.AddPeer(0x42, new GossipTimestampFilter(0, uint.MaxValue));
        sender.Stalled = slow;
        await relay.BaselineAsync();
        relay._graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await relay.FlushAllAsync();
        relay._graph.AddSignedChannel(s_scid2, SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        await relay.FlushAllAsync();

        // Assert: the fast peer got both channels, the stalled one was not asked again while its send hangs
        Assert.Equal([s_scid1, s_scid2],
                     fast.Sent.OfType<ChannelAnnouncementMessage>().Select(m => m.Payload.ShortChannelId));
        Assert.Equal(1, sender.StalledAttempts);
        Assert.Empty(slow.Sent);

        // The stalled send ends: that peer's run finishes its flush
        sender.Release();
        for (var i = 0; i < 100 && !slow.Sent.Any(m => m is ChannelUpdateMessage); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Contains(slow.Sent, m => m is ChannelUpdateMessage);
    }

    [Fact]
    public async Task Given_AFilterWindow_When_UpdatesAtTheBoundariesAreRelayed_Then_FirstIsInAndFirstPlusRangeIsOut()
    {
        // Arrange (B7-Q-05: relay only first_timestamp <= ts < first_timestamp + timestamp_range)
        var peer = AddPeer(0x41, new GossipTimestampFilter(1_000, 100));
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 999, null);
        _graph.AddSignedChannel(s_scid2, SyncTestGraph.NodeA, SyncTestGraph.NodeC, 1_000, null);
        _graph.AddSignedChannel(s_scid3, SyncTestGraph.NodeB, SyncTestGraph.NodeC, 1_099, null);
        _graph.AddSignedChannel(s_scid4, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 1_100, null);

        // Act
        await FlushAllAsync();

        // Assert: the announcement takes its update's timestamp (never sent without one inside)
        var updates = peer.Sent.OfType<ChannelUpdateMessage>().Select(m => m.Payload.Timestamp).ToList();
        Assert.Equal([1_000u, 1_099u], updates);
        var announced = peer.Sent.OfType<ChannelAnnouncementMessage>().Select(m => m.Payload.ShortChannelId);
        Assert.Equal([s_scid2, s_scid3], announced);
    }

    [Fact]
    public async Task Given_TheNothingFilter_When_GossipIsAccepted_Then_NothingIsRelayed()
    {
        // Arrange (B7-Q-06: 0xFFFFFFFF / 0 lets nothing through)
        var peer = AddPeer(0x41, GossipTimestampFilter.None);
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public async Task Given_APeerThatSentAMessage_When_Flushed_Then_ThatVersionNeverGoesBackToIt()
    {
        // Arrange (origin suppression: never send a message back to the peer we received it from)
        var origin = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        var other = AddPeer(0x42, new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 1_700_000_000, null);
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        _origins.Record(GraphTestKit.SignedChannelAnnouncement(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                                                               new TestGossipKey(0x31), new TestGossipKey(0x32)),
                        origin.PeerPubKey);
        _origins.Record(GraphTestKit.SignedChannelUpdate(s_scid1, node1, 0, 1_700_000_000), origin.PeerPubKey);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Empty(origin.Sent);
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate], other.Sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Given_AnOriginOfAnOlderVersion_When_ANewerVersionIsFlushed_Then_TheOriginGetsTheNewerOne()
    {
        // Arrange: the peer sent the version at t, the graph then took t + 10 from someone else
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 1_700_000_000, null);
        await BaselineAsync();
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        _origins.Record(GraphTestKit.SignedChannelUpdate(s_scid1, node1, 0, 1_700_000_000), peer.PeerPubKey);
        ApplyUpdate(s_scid1, node1, 0, 1_700_000_010);

        // Act
        await FlushAllAsync();

        // Assert
        var update = Assert.IsType<ChannelUpdateMessage>(Assert.Single(peer.Sent));
        Assert.Equal(1_700_000_010u, update.Payload.Timestamp);
    }

    [Fact]
    public async Task Given_SeveralUpdatesBetweenFlushes_When_Flushed_Then_OnlyTheNewestGoesOut()
    {
        // Arrange
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 1_700_000_000, null);
        await BaselineAsync();
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act: two collects before the peer's flush
        ApplyUpdate(s_scid1, node1, 0, 1_700_000_010);
        await TickAfterAsync(TimeSpan.FromSeconds(10));
        ApplyUpdate(s_scid1, node1, 0, 1_700_000_020);
        await FlushAllAsync();

        // Assert (the peer's phase is past the first collect, so its first flush comes after both)
        Assert.True(_relay.GetRelayPhase(peer.PeerPubKey) > TimeSpan.FromSeconds(20));
        var update = Assert.IsType<ChannelUpdateMessage>(Assert.Single(peer.Sent));
        Assert.Equal(1_700_000_020u, update.Payload.Timestamp);
    }

    [Fact]
    public async Task Given_AStoredGraph_When_TheRelayStarts_Then_TheStoredGraphIsNotRelayedAgain()
    {
        // Arrange: what the graph held at the first scan is the baseline (the peers' filters ask for it)
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await BaselineAsync();
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public async Task Given_SpentUnverifiedOrDontForwardGossip_When_Flushed_Then_NoneIsRelayed()
    {
        // Arrange (B7-Q-05: SHOULD NOT send spent channels; unverified channels are never relayed, plan §3.4)
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, spentAtHeight: 600);
        _graph.AddSignedChannel(s_scid2, SyncTestGraph.NodeA, SyncTestGraph.NodeC,
                                verification: GraphChannelVerification.Unverified);
        _graph.AddSignedChannel(s_scid3, SyncTestGraph.NodeB, SyncTestGraph.NodeC, null, null);
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeB, SyncTestGraph.NodeC);
        var dontForward = GraphTestKit.SignedChannelUpdate(s_scid3, node1, 0, 1_700_000_000,
                                                           messageFlags: ChannelUpdatePayload.MessageFlagMustBeOne
                                                                       | ChannelUpdatePayload.MessageFlagDontForward)
                                      .Payload;
        _graph.Store.TryApplyPolicy(s_scid3, GraphPolicy.FromChannelUpdate(dontForward) with
        {
            RawUpdate = dontForward.GetBytes()
        });

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public async Task Given_APeerOnAnotherChain_When_Flushed_Then_ItGetsNothing()
    {
        // Arrange (B7-RL-01: SHOULD NOT forward to a peer whose init networks exclude the chain)
        var peer = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        peer.Features.ChainHashes = [ChainConstants.Main];
        await BaselineAsync();
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public async Task Given_TwoPeers_When_TheClockPassesEachPhase_Then_EachIsFlushedAtItsOwnOffset()
    {
        // Arrange (BOLT 7: SHOULD flush every 60 s, staggered)
        var early = AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        var late = AddPeer(0x42, new GossipTimestampFilter(0, uint.MaxValue));
        var (first, second) = _relay.GetRelayPhase(early.PeerPubKey) <= _relay.GetRelayPhase(late.PeerPubKey)
                                  ? (early, late)
                                  : (late, early);
        var firstPhase = _relay.GetRelayPhase(first.PeerPubKey);
        var secondPhase = _relay.GetRelayPhase(second.PeerPubKey);
        Assert.NotEqual(firstPhase, secondPhase);
        await BaselineAsync(); // states created now, flush phases counted from here
        _graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        await TickAfterAsync(TimeSpan.FromSeconds(10)); // collected
        var start = TimeSpan.FromSeconds(10);

        Assert.True(firstPhase > start); // both phases lie after the collect, so neither peer was flushed yet

        // Act / Assert: nothing before a peer's phase, its flush at it, the other peer still waiting
        await TickAfterAsync(firstPhase - start - TimeSpan.FromTicks(1));
        Assert.Empty(first.Sent);
        await TickAfterAsync(TimeSpan.FromTicks(1));
        Assert.NotEmpty(first.Sent);
        Assert.Empty(second.Sent);
        await TickAfterAsync(secondPhase - firstPhase);
        Assert.NotEmpty(second.Sent);
    }

    [Fact]
    public async Task Given_ANewFilter_When_TheBacklogRuns_Then_TheGraphInsideTheFilterGoesOutPacedAndInOrder()
    {
        // Arrange: a stored graph, then the peer's filter (the backlog of plan §3.7), 2 messages per second
        using var paced = new GossipRelayOthersTests(new GossipRelayOptions { BacklogMessagesPerSecond = 2 });
        paced._graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 1_700_000_000, 1_700_000_001);
        paced._graph.AddSignedChannel(s_scid2, SyncTestGraph.NodeA, SyncTestGraph.NodeC, 1_600_000_000, 1_600_000_001);
        paced._graph.AddNode(SyncTestGraph.NodeA, 1_700_000_000);
        paced._graph.AddNode(SyncTestGraph.NodeC, 1_000);
        var peer = paced.AddPeer(0x41, new GossipTimestampFilter(1_650_000_000, uint.MaxValue));
        await paced.BaselineAsync();

        // Act
        paced.RaiseFilter(peer);
        var first = await paced.TickAfterAsync(TimeSpan.FromSeconds(1));
        var afterFirst = peer.Sent.Count;
        await paced.TickAfterAsync(TimeSpan.FromSeconds(1));
        await paced.TickAfterAsync(TimeSpan.FromSeconds(1));

        // Assert: only scid1 (its updates are inside), its announcement then both updates, then node A (C is too old)
        Assert.Equal(2, first);
        Assert.Equal(2, afterFirst);
        Assert.Equal([
            MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
            MessageTypes.NodeAnnouncement
        ], peer.Sent.Select(m => m.Type));
        Assert.Equal(s_scid1, peer.Sent.OfType<ChannelAnnouncementMessage>().Single().Payload.ShortChannelId);
        Assert.Equal(SyncTestGraph.NodeA.PubKey, peer.Sent.OfType<NodeAnnouncementMessage>().Single().Payload.NodeId);
        Assert.Equal(1, paced._clock.TimersCreated);
    }

    [Fact]
    public async Task Given_OurOwnChannel_When_Relayed_Then_OnlyThePeersDirectionGoesThroughTheRelay()
    {
        // Arrange: we are node A; our 256 and 258 go through the own path (regardless of filters)
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodePubKey()).Returns(SyncTestGraph.NodeA.PubKey);
        using var ours = new GossipRelayOthersTests(new GossipRelayOptions(), keyManager: keyManager.Object);
        var peer = ours.AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await ours.BaselineAsync();
        ours._graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        ours._graph.AddNode(SyncTestGraph.NodeA);

        // Act
        await ours.FlushAllAsync();

        // Assert
        var update = Assert.IsType<ChannelUpdateMessage>(Assert.Single(peer.Sent));
        var nodeB = update.Payload.Direction ? 1 : 0;
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        Assert.Equal(node1 == SyncTestGraph.NodeB ? 0 : 1, nodeB);
    }

    [Fact]
    public async Task Given_APeerOutbox_When_RelayedAndOwnGossipIsSent_Then_BothAreQueuedOnTheOutbox()
    {
        // Arrange (NL-351: own and relayed gossip through the peer's PeerOutbox)
        var outbox = new RecordingOutbox();
        var services = new ServiceCollection().AddSingleton<IPeerGossipOutbox>(outbox).BuildServiceProvider();
        using var withOutbox = new GossipRelayOthersTests(new GossipRelayOptions(), new PeerGossipSender(services));
        var peer = withOutbox.AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await withOutbox.BaselineAsync();
        withOutbox._graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        withOutbox._relay.EnqueueOwnChannelUpdate(
            GraphTestKit.SignedChannelUpdate(s_scid2, SyncTestGraph.NodeC, 0, 1_700_000_000).Payload);

        // Act
        await withOutbox.FlushAllAsync();
        await withOutbox._relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(peer.Sent);
        Assert.All(outbox.Queued, q => Assert.Same(peer, q.Connection));
        Assert.Equal([
            MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
            MessageTypes.ChannelUpdate
        ], outbox.Queued.Select(q => q.Message.Type));
    }

    [Fact]
    public async Task Given_AClosedOutbox_When_Flushed_Then_TheRestOfThatFlushIsNotSent()
    {
        // Arrange
        var outbox = new RecordingOutbox { Accept = false };
        var services = new ServiceCollection().AddSingleton<IPeerGossipOutbox>(outbox).BuildServiceProvider();
        using var closed = new GossipRelayOthersTests(new GossipRelayOptions(), new PeerGossipSender(services));
        closed.AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await closed.BaselineAsync();
        closed._graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await closed.FlushAllAsync();

        // Assert: the first refusal ends the flush for that connection
        Assert.Equal(1, outbox.Attempts);
    }

    [Fact]
    public async Task Given_RelayDisabled_When_Ticked_Then_NothingIsCollectedOrSent()
    {
        // Arrange (plan D12: Gossip:RelayEnabled)
        using var off = new GossipRelayOthersTests(new GossipRelayOptions { RelayEnabled = false });
        var peer = off.AddPeer(0x41, new GossipTimestampFilter(0, uint.MaxValue));
        await off.BaselineAsync();
        off._graph.AddSignedChannel(s_scid1, SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await off.FlushAllAsync();

        // Assert
        Assert.False(off._relay.IsRelayingOthers);
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public void Given_Mainnet_When_RelayEnabledIsUnset_Then_TheRelayIsOff()
    {
        // Act / Assert (plan D12)
        Assert.False(new GossipRelayOptions().IsRelayEnabledFor(BitcoinNetwork.Mainnet));
        Assert.True(new GossipRelayOptions().IsRelayEnabledFor(BitcoinNetwork.Regtest));
        Assert.True(new GossipRelayOptions { RelayEnabled = true }.IsRelayEnabledFor(BitcoinNetwork.Mainnet));
    }

    public void Dispose() => _relay.Dispose();

    private FakeGossipPeer AddPeer(byte seed, GossipTimestampFilter? filter)
    {
        var peer = new FakeGossipPeer(seed);
        _peers.Add(new GossipPeer(peer.PeerPubKey, peer));
        if (filter is { } f)
            _filters[peer] = f;
        return peer;
    }

    private void RaiseFilter(FakeGossipPeer peer) =>
        _syncManager.Raise(m => m.FilterReceived += null, new GossipFilterReceivedEventArgs(peer, _filters[peer]));

    private void ApplyUpdate(ShortChannelId shortChannelId, TestGossipKey origin, byte direction, uint timestamp)
    {
        var update = GraphTestKit.SignedChannelUpdate(shortChannelId, origin, direction, timestamp).Payload;
        Assert.True(_graph.Store.TryApplyPolicy(shortChannelId, GraphPolicy.FromChannelUpdate(update) with
        {
            RawUpdate = update.GetBytes()
        }));
    }

    /// <summary>The first tick: records the graph as it is (and creates the peers' states at this time).</summary>
    private Task<int> BaselineAsync() => _relay.RelayTickAsync(TestContext.Current.CancellationToken);

    /// <summary>A collect and a full flush interval: every peer with a filter is flushed once.</summary>
    private async Task FlushAllAsync()
    {
        await TickAfterAsync(TimeSpan.FromSeconds(10));
        await TickAfterAsync(s_flushInterval);
    }

    private Task<int> TickAfterAsync(TimeSpan by)
    {
        _clock.Advance(by);
        return _relay.RelayTickAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Sends straight to the peer, except that every send to <see cref="Stalled"/> hangs until released.</summary>
    private sealed class StallingSender : IGossipPeerSender
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stalledAttempts;

        public IPeerService? Stalled { get; set; }
        public int StalledAttempts => Volatile.Read(ref _stalledAttempts);

        public void Release() => _released.TrySetResult();

        public async ValueTask<bool> SendAsync(GossipPeer peer, IMessage message)
        {
            if (ReferenceEquals(peer.Service, Stalled) && !_released.Task.IsCompleted)
            {
                Interlocked.Increment(ref _stalledAttempts);
                await _released.Task;
            }

            await peer.Service.SendGossipMessageAsync(message);
            return true;
        }
    }

    private sealed class RecordingOutbox : IPeerGossipOutbox
    {
        public bool Accept { get; init; } = true;
        public int Attempts { get; private set; }
        public List<(IPeerService Connection, IMessage Message)> Queued { get; } = [];

        public bool TryEnqueueGossip(IPeerService connection, IMessage message)
        {
            Attempts++;
            if (!Accept)
                return false;

            Queued.Add((connection, message));
            return true;
        }
    }
}