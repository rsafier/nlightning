using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Metrics;
using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Graph;
using Metrics;
using Sync;

/// <summary>
/// BOLT 7 plan G5-T2: the relay keeps at most <see cref="GossipRelayOptions.MaxRelayPendingPerPeer"/> messages waiting
/// for one connection's flush, dropping the oldest first (counted), so a slow peer never gets more than that per flush
/// on its outbox; and the relayed/dropped counters (G5-T4).
/// </summary>
public class GossipRelayBacklogBoundTests : IDisposable
{
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromSeconds(60);

    private readonly SyncTestGraph _graph = new();
    private readonly RelayTestClock _clock = new();
    private readonly List<GossipPeer> _peers = [];
    private readonly Dictionary<IPeerService, GossipTimestampFilter> _filters = new(ReferenceEqualityComparer.Instance);
    private readonly Mock<IGossipSyncManager> _syncManager = new();
    private readonly GossipMetrics _metrics = new();
    private readonly GossipMetricsRecorder _recorder;
    private readonly GossipRelayScheduler _relay;

    public GossipRelayBacklogBoundTests() : this(null)
    {
    }

    private GossipRelayBacklogBoundTests(IGossipPeerSender? sender)
    {
        _recorder = new GossipMetricsRecorder(_metrics);
        _syncManager.Setup(m => m.TryGetPeerFilter(It.IsAny<IPeerService>(), out It.Ref<GossipTimestampFilter>.IsAny))
                    .Returns(new TryGetFilter((IPeerService peer, out GossipTimestampFilter filter) =>
                                                  _filters.TryGetValue(peer, out filter)));
        var directory = new Mock<IGossipPeerDirectory>();
        directory.Setup(d => d.GetConnectedPeers()).Returns(() => _peers.ToList());
        var options = new GossipRelayOptions
        {
            RelayFlushInterval = s_flushInterval,
            RelayCollectInterval = TimeSpan.FromSeconds(10),
            RelayTickInterval = TimeSpan.FromSeconds(1),
            MaxRelayPendingPerPeer = 3
        };
        _relay = new GossipRelayScheduler(directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                          Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                          Options.Create(new GossipOptions()), _clock, sender, _graph.Store,
                                          _syncManager.Object, new GossipOriginTracker(), Options.Create(options),
                                          metrics: _metrics);
    }

    private delegate bool TryGetFilter(IPeerService peer, out GossipTimestampFilter filter);

    public void Dispose()
    {
        _relay.Dispose();
        _recorder.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task Given_MoreNewGossipThanTheCap_When_Flushed_Then_OnlyTheNewestCapMessagesGoOutAndTheRestIsCounted()
    {
        // Arrange: five channels (each an announcement and two updates: 15 messages) accepted in one collect window
        var peer = AddPeer(0x41);
        await _relay.RelayTickAsync(TestContext.Current.CancellationToken);
        for (uint i = 0; i < 5; i++)
            _graph.AddSignedChannel(new ShortChannelId(600 + i, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);

        // Act
        await FlushAllAsync();

        // Assert: the newest three (one channel's announcement and both its updates) went out, twelve were dropped
        Assert.Equal(3, peer.Sent.Count);
        Assert.Single(peer.Sent.OfType<ChannelAnnouncementMessage>());
        Assert.Equal(2, peer.Sent.OfType<ChannelUpdateMessage>().Count());
        var scid = peer.Sent.OfType<ChannelAnnouncementMessage>().Single().Payload.ShortChannelId;
        Assert.All(peer.Sent.OfType<ChannelUpdateMessage>(), u => Assert.Equal(scid, u.Payload.ShortChannelId));
        Assert.Equal(12, _recorder.Sum("nlightning.gossip.messages.dropped",
                                       (GossipMetrics.ReasonTag, GossipMetricReasons.RelayBacklogFull)));
        Assert.Equal(3, _recorder.Sum("nlightning.gossip.messages.relayed", (GossipMetrics.PathTag, "others")));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.relayed",
                                      (GossipMetrics.TypeTag, "channel_announcement")));
        Assert.Equal(0, _recorder.ObserveQueue("relay_pending"));
    }

    [Fact]
    public async Task Given_ASlowPeer_When_GossipKeepsComing_Then_EachFlushQueuesAtMostTheCapOnItsOutbox()
    {
        // Arrange: the outbox accepts everything and never drains (a peer that stopped reading)
        var outbox = new CountingSender();
        using var relay = new GossipRelayBacklogBoundTests(outbox);
        var peer = relay.AddPeer(0x41);
        await relay._relay.RelayTickAsync(TestContext.Current.CancellationToken);

        // Act: three rounds of five new channels each
        var perFlush = new List<int>();
        for (uint round = 0; round < 3; round++)
        {
            for (uint i = 0; i < 5; i++)
                relay._graph.AddSignedChannel(new ShortChannelId(700 + round * 10 + i, 1, 0), SyncTestGraph.NodeA,
                                              SyncTestGraph.NodeC);
            var before = outbox.Queued;
            await relay.FlushAllAsync();
            perFlush.Add(outbox.Queued - before);
        }

        // Assert
        Assert.Equal([3, 3, 3], perFlush);
        Assert.Empty(peer.Sent);
        Assert.Equal(36, relay._recorder.Sum("nlightning.gossip.messages.dropped",
                                             (GossipMetrics.ReasonTag, GossipMetricReasons.RelayBacklogFull)));
    }

    [Fact]
    public async Task Given_AnUpdateReplacedWhileWaiting_When_TheCapIsReached_Then_TheReplacedKeyCountsAsNew()
    {
        // Arrange: a newer version of a waiting message replaces it and moves to the back of the line; a peer whose
        // flush phase falls after two collects
        var seed = Enumerable.Range(0x41, 100).Select(i => (byte)i)
                             .First(b => _relay.GetRelayPhase(new FakeGossipPeer(b).PeerPubKey) is var phase
                                      && phase > TimeSpan.FromSeconds(25) && phase < TimeSpan.FromSeconds(55));
        var peer = AddPeer(seed);
        await _relay.RelayTickAsync(TestContext.Current.CancellationToken);
        var first = new ShortChannelId(800, 1, 0);
        _graph.AddSignedChannel(first, SyncTestGraph.NodeA, SyncTestGraph.NodeB, 1_700_000_000, null);
        await TickAfterAsync(TimeSpan.FromSeconds(10));
        var (node1, _) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var newer = GraphTestKit.SignedChannelUpdate(first, node1, 0, 1_700_000_100, 7_000).Payload;
        Assert.True(_graph.Store.TryApplyPolicy(first, Domain.Gossip.Graph.GraphPolicy.FromChannelUpdate(newer) with
        {
            RawUpdate = newer.GetBytes()
        }));
        _graph.AddSignedChannel(new ShortChannelId(801, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC,
                                1_700_000_000, null);

        // Act: the second collect, then the flush
        await TickAfterAsync(TimeSpan.FromSeconds(10));
        await TickAfterAsync(TimeSpan.FromSeconds(50));

        // Assert: the pending set held first's 256 + 258 then (replaced) 258, 801's 256 + 258: the cap of 3 kept
        // first's newer 258 and 801's pair, and dropped first's announcement (the oldest)
        var updates = peer.Sent.OfType<ChannelUpdateMessage>().ToList();
        Assert.Contains(updates, u => u.Payload.ShortChannelId == first && u.Payload.Timestamp == 1_700_000_100);
        Assert.DoesNotContain(peer.Sent.OfType<ChannelAnnouncementMessage>(), a => a.Payload.ShortChannelId == first);
    }

    private FakeGossipPeer AddPeer(byte seed)
    {
        var peer = new FakeGossipPeer(seed);
        _peers.Add(new GossipPeer(peer.PeerPubKey, peer));
        _filters[peer] = new GossipTimestampFilter(0, uint.MaxValue);
        return peer;
    }

    /// <summary>
    /// One flush interval: a collect tick 10 s in, then the tick at its end. With the baseline at the round's start,
    /// every connection's phase (in [0, 60 s)) falls in exactly one round, after that round's collect.
    /// </summary>
    private async Task FlushAllAsync()
    {
        await TickAfterAsync(TimeSpan.FromSeconds(10));
        await TickAfterAsync(s_flushInterval - TimeSpan.FromSeconds(10));
    }

    private Task<int> TickAfterAsync(TimeSpan by)
    {
        _clock.Advance(by);
        return _relay.RelayTickAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An outbox that takes every message and sends none (a peer that stopped reading).</summary>
    private sealed class CountingSender : IGossipPeerSender
    {
        private int _queued;

        public int Queued => Volatile.Read(ref _queued);

        public ValueTask<bool> SendAsync(GossipPeer peer, Domain.Protocol.Interfaces.IMessage message)
        {
            Interlocked.Increment(ref _queued);
            return ValueTask.FromResult(true);
        }
    }
}