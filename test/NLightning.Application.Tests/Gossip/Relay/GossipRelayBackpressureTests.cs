using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Metrics;
using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync.Interfaces;
using Application.Node.Services;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Graph;
using Metrics;
using Sync;

/// <summary>
/// NL-360: the relay pauses a connection whose outbox is at its gossip cap and resumes it once the outbox drained to
/// <see cref="GossipRelayOptions.RelayResumePercent"/>, keeping its place in the backlog and the rest of an
/// interrupted flush (256 before 258, nothing lost, nothing sent twice); a paused connection that sends nothing for
/// <see cref="GossipRelayOptions.RelayStallTimeout"/> stalls (backlog ended, pending dropped); our own gossip refused
/// by a full outbox goes out at a later flush.
/// </summary>
public class GossipRelayBackpressureTests : IDisposable
{
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_stallTimeout = TimeSpan.FromMinutes(5);

    private readonly SyncTestGraph _graph = new();
    private readonly RelayTestClock _clock = new();
    private readonly List<GossipPeer> _peers = [];
    private readonly Dictionary<IPeerService, GossipTimestampFilter> _filters = new(ReferenceEqualityComparer.Instance);
    private readonly Mock<IGossipSyncManager> _syncManager = new();
    private readonly GossipMetrics _metrics = new();
    private readonly GossipMetricsRecorder _recorder;
    private readonly CappedOutboxSender _outbox;
    private readonly GossipRelayScheduler _relay;

    public GossipRelayBackpressureTests() : this(4, 5_000)
    {
    }

    private GossipRelayBackpressureTests(IGossipPeerSender sender) : this(1, 5_000, sender)
    {
    }

    private GossipRelayBackpressureTests(int outboxCap, int maxPending, IGossipPeerSender? sender = null)
    {
        _recorder = new GossipMetricsRecorder(_metrics);
        _outbox = new CappedOutboxSender(outboxCap);
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
            MaxRelayPendingPerPeer = maxPending,
            RelayStallTimeout = s_stallTimeout
        };
        _relay = new GossipRelayScheduler(directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                          Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                          Options.Create(new GossipOptions()), _clock, sender ?? _outbox, _graph.Store,
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
    public async Task Given_ABacklogLargerThanTheOutboxCap_When_ThePeerReadsSlowly_Then_ItPausesResumesAndLosesNothing()
    {
        // Arrange: ten channels (30 messages) in the graph, an outbox that holds 4
        for (uint i = 0; i < 10; i++)
            _graph.AddSignedChannel(new ShortChannelId(600 + i, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var peer = AddPeer(0x41);
        await TickAsync(TimeSpan.Zero);

        // Act: the filter asks for the backlog; the outbox fills, then the peer reads one message per tick
        RaiseFilter(peer);
        await TickAsync(TimeSpan.FromSeconds(1));
        var queuedAtFirstPause = _outbox.QueuedFor(peer);
        var pausedGauge = _recorder.Observe("nlightning.gossip.relay.paused_connections");
        await TickAsync(TimeSpan.FromSeconds(1));
        var queuedWhilePausedWithoutReading = _outbox.QueuedFor(peer);
        _outbox.Drain(peer, 1);
        await TickAsync(TimeSpan.FromSeconds(1));
        var queuedAboveTheResumeMark = _outbox.QueuedFor(peer);
        for (var i = 0; i < 100 && (peer.Sent.Count < 30 || _outbox.QueuedFor(peer) > 0); i++)
        {
            _outbox.Drain(peer, 1);
            await TickAsync(TimeSpan.FromSeconds(1));
        }

        // Assert: paused at the cap, resumed only at half of it, the whole backlog delivered once and in order
        Assert.Equal(4, queuedAtFirstPause);
        Assert.Equal(1, pausedGauge);
        Assert.Equal(4, queuedWhilePausedWithoutReading);
        Assert.Equal(3, queuedAboveTheResumeMark);
        Assert.Equal(30, peer.Sent.Count);
        Assert.Equal(10, peer.Sent.OfType<ChannelAnnouncementMessage>().Select(a => a.Payload.ShortChannelId)
                                 .Distinct().Count());
        AssertEveryUpdateAfterItsAnnouncement(peer.Sent);
        Assert.True(_recorder.Sum("nlightning.gossip.relay.paused") >= 2);
        Assert.Equal(0, _recorder.Observe("nlightning.gossip.relay.paused_connections"));
        Assert.Equal(0, _recorder.Sum("nlightning.gossip.messages.dropped"));
    }

    [Fact]
    public async Task Given_AFlushInterruptedByAFullOutbox_When_ItDrains_Then_TheRestGoesOutWithoutWaitingAFlushInterval()
    {
        // Arrange: five new channels (15 messages) collected for a peer whose outbox holds 4
        var peer = AddPeer(0x41);
        await TickAsync(TimeSpan.Zero);
        for (uint i = 0; i < 5; i++)
            _graph.AddSignedChannel(new ShortChannelId(700 + i, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        await FlushAllAsync();
        var pendingWhilePaused = _recorder.ObserveQueue("relay_pending");

        // Act: the peer reads everything; the relay goes on at the next ticks, well inside one flush interval
        var ticks = 0;
        while (peer.Sent.Count < 15 && ticks++ < 20)
        {
            _outbox.Drain(peer, int.MaxValue);
            await TickAsync(TimeSpan.FromSeconds(1));
        }

        _outbox.Drain(peer, int.MaxValue);

        // Assert: 4 queued before the pause, 11 kept; all 15 delivered once, announcements first
        Assert.Equal(11, pendingWhilePaused);
        Assert.True(ticks < 20);
        Assert.Equal(15, peer.Sent.Count);
        Assert.Equal(5, peer.Sent.OfType<ChannelAnnouncementMessage>().Count());
        AssertEveryUpdateAfterItsAnnouncement(peer.Sent);
        Assert.Equal(0, _recorder.Sum("nlightning.gossip.messages.dropped"));
    }

    [Fact]
    public async Task Given_APutBackOverThePendingBound_When_Evicted_Then_NoUpdateGoesOutWithoutItsAnnouncement()
    {
        // Arrange: an outbox of 1 and a pending bound of 6; three channels (9 messages, one channel evicted at the
        // collect), the flush pauses after one message and puts the rest back
        using var tight = new GossipRelayBackpressureTests(1, 6);
        var peer = tight.AddPeer(0x41);
        await tight.TickAsync(TimeSpan.Zero);
        for (uint i = 0; i < 3; i++)
            tight._graph.AddSignedChannel(new ShortChannelId(800 + i, 1, 0), SyncTestGraph.NodeA,
                                          SyncTestGraph.NodeB);
        await tight.FlushAllAsync();
        Assert.Equal(1, tight._outbox.QueuedFor(peer));

        // Act: a new channel is collected while the connection is paused (the bound evicts the put-back first), then
        // the peer reads everything
        tight._graph.AddSignedChannel(new ShortChannelId(900, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        for (var i = 0; i < 150; i++)
        {
            tight._outbox.Drain(peer, int.MaxValue);
            await tight.TickAsync(TimeSpan.FromSeconds(1));
        }

        tight._outbox.Drain(peer, int.MaxValue);

        // Assert: what the bound dropped never leaves an update without its announcement; nothing went twice
        Assert.True(tight._recorder.Sum("nlightning.gossip.messages.dropped",
                                        (GossipMetrics.ReasonTag, GossipMetricReasons.RelayBacklogFull)) > 0);
        Assert.Contains(peer.Sent.OfType<ChannelAnnouncementMessage>(),
                        a => a.Payload.ShortChannelId == new ShortChannelId(900, 1, 0));
        AssertEveryUpdateAfterItsAnnouncement(peer.Sent);
        var keys = peer.Sent.Select(KeyOf).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task Given_APausedPeerThatNeverReads_When_TheStallTimeoutPasses_Then_ItsBacklogEndsAndItResumesLater()
    {
        // Arrange: a backlog of ten channels; the outbox fills and the peer stops reading
        for (uint i = 0; i < 10; i++)
            _graph.AddSignedChannel(new ShortChannelId(600 + i, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var peer = AddPeer(0x41);
        await TickAsync(TimeSpan.Zero);
        RaiseFilter(peer);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(4, _outbox.QueuedFor(peer));

        // Act: just before the timeout nothing stalls; at it the backlog ends
        await TickAsync(s_stallTimeout - TimeSpan.FromSeconds(1));
        var stalledBefore = _recorder.Sum("nlightning.gossip.relay.stalled");
        await TickAsync(TimeSpan.FromSeconds(1));
        var stalledAt = _recorder.Sum("nlightning.gossip.relay.stalled");
        await TickAsync(s_stallTimeout);
        var stalledAgain = _recorder.Sum("nlightning.gossip.relay.stalled");
        for (var i = 0; i < 5; i++)
        {
            _outbox.Drain(peer, int.MaxValue);
            await TickAsync(TimeSpan.FromSeconds(1));
        }

        var afterStall = peer.Sent.Count;
        _graph.AddSignedChannel(new ShortChannelId(950, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        await FlushAllAsync();
        _outbox.Drain(peer, int.MaxValue);

        // Assert: one stall, the backlog did not continue after the drain, new gossip flows again
        Assert.Equal(0, stalledBefore);
        Assert.Equal(1, stalledAt);
        Assert.Equal(1, stalledAgain);
        Assert.Equal(4, afterStall);
        Assert.Equal(7, peer.Sent.Count);
        Assert.Contains(peer.Sent.OfType<ChannelAnnouncementMessage>(),
                        a => a.Payload.ShortChannelId == new ShortChannelId(950, 1, 0));
        Assert.Equal(0, _recorder.Observe("nlightning.gossip.relay.paused_connections"));
    }

    [Fact]
    public async Task Given_APausedPeerThatReadsSlowly_When_TheStallTimeoutPasses_Then_ItDoesNotStall()
    {
        // Arrange: an outbox of 4 filled by the backlog; the peer reads one message per minute
        for (uint i = 0; i < 10; i++)
            _graph.AddSignedChannel(new ShortChannelId(600 + i, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var peer = AddPeer(0x41);
        await TickAsync(TimeSpan.Zero);
        RaiseFilter(peer);
        await TickAsync(TimeSpan.FromSeconds(1));

        // Act
        for (var minute = 0; minute < 12; minute++)
        {
            _outbox.Drain(peer, 1);
            await TickAsync(TimeSpan.FromMinutes(1));
        }

        // Assert: it made progress, so no stall, and the backlog went on
        Assert.Equal(0, _recorder.Sum("nlightning.gossip.relay.stalled"));
        Assert.Equal(12, peer.Sent.Count);
        Assert.Equal(4, _outbox.QueuedFor(peer));
    }

    [Fact]
    public async Task Given_OurOwnGossipAndAFullOutbox_When_Flushed_Then_TheRefusedMessageGoesOutAtALaterFlush()
    {
        // Arrange: an outbox of 1, our announcement and update
        using var one = new GossipRelayBackpressureTests(1, 5_000);
        var peer = one.AddPeer(0x41);
        var scid = new ShortChannelId(990, 1, 0);
        one._relay.EnqueueOwnChannelAnnouncement(
            GraphTestKit.SignedChannelAnnouncement(scid, SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                                                   new TestGossipKey(0x31), new TestGossipKey(0x32)).Payload);
        one._relay.EnqueueOwnChannelUpdate(
            GraphTestKit.SignedChannelUpdate(scid, SyncTestGraph.NodeA, 0, 1_700_000_000).Payload);

        // Act
        await one._relay.FlushAsync(TestContext.Current.CancellationToken);
        var queuedFirst = one._outbox.QueuedFor(peer);
        one._outbox.Drain(peer, int.MaxValue);
        await one._relay.FlushAsync(TestContext.Current.CancellationToken);
        one._outbox.Drain(peer, int.MaxValue);
        await one._relay.FlushAsync(TestContext.Current.CancellationToken);
        one._outbox.Drain(peer, int.MaxValue);

        // Assert: the announcement, then (next flush) its update, each once
        Assert.Equal(1, queuedFirst);
        Assert.Equal([typeof(ChannelAnnouncementMessage), typeof(ChannelUpdateMessage)],
                     peer.Sent.Select(m => m.GetType()));
    }

    [Fact]
    public async Task Given_TheRealPeerOutboxAndASlowReader_When_TheBacklogRuns_Then_ChannelMessagesPassAndTheBacklogCompletes()
    {
        // Arrange: the production PeerOutbox (cap 5 messages) through PeerGossipSender; the peer's transport takes a
        // gossip message only when the test releases one
        var peer = new FakeGossipPeer(0x41);
        var gate = new SemaphoreSlim(0);
        var wire = new List<object>();
        var transport = new Mock<IPeerService>();
        transport.SetupGet(p => p.PeerPubKey).Returns(peer.PeerPubKey);
        transport.Setup(p => p.SendGossipMessageAsync(It.IsAny<IMessage>()))
                 .Returns(async (IMessage message) =>
                  {
                      await gate.WaitAsync();
                      lock (wire)
                          wire.Add(message);
                  });
        transport.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                 .Returns((IChannelMessage message) =>
                  {
                      lock (wire)
                          wire.Add(message);
                      return Task.CompletedTask;
                  });
        var outbox = new PeerOutbox(transport.Object, NullLogger.Instance, 5);
        var port = new Mock<IPeerGossipOutbox>();
        port.Setup(p => p.EnqueueGossip(peer, It.IsAny<IMessage>(), It.IsAny<int>()))
            .Returns((IPeerService _, IMessage message, int size) => outbox.EnqueueGossip(message, size));
        port.Setup(p => p.GetGossipDepth(peer)).Returns(() => outbox.GossipDepth);
        var services = new ServiceCollection().AddSingleton(port.Object).BuildServiceProvider();
        using var real = new GossipRelayBackpressureTests(new PeerGossipSender(services));
        for (uint i = 0; i < 8; i++)
            real._graph.AddSignedChannel(new ShortChannelId(600 + i, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        real._peers.Add(new GossipPeer(peer.PeerPubKey, peer));
        real._filters[peer] = new GossipTimestampFilter(0, uint.MaxValue);
        await real.TickAsync(TimeSpan.Zero);
        real.RaiseFilter(peer);
        await real.TickAsync(TimeSpan.FromSeconds(1));

        // Act: a channel message while the gossip share is full, then the peer reads everything, slowly
        var channelMessage = new Mock<IChannelMessage>().Object;
        var channelQueued = outbox.TryEnqueue(channelMessage);
        var refusedAtPause = outbox.RefusedGossipCount;
        for (var i = 0; i < 200; i++)
        {
            gate.Release();
            await Task.Delay(1, TestContext.Current.CancellationToken);
            await real.TickAsync(TimeSpan.FromSeconds(1));
            lock (wire)
                if (wire.Count == 25)
                    break;
        }

        // Assert: the channel message was queued (never refused for gossip) and the whole backlog went out in order
        Assert.True(channelQueued);
        Assert.True(refusedAtPause >= 1);
        List<object> sent;
        lock (wire)
            sent = wire.ToList();
        Assert.Equal(25, sent.Count);
        Assert.Contains(channelMessage, sent);
        var gossip = sent.OfType<IMessage>().Where(m => !ReferenceEquals(m, channelMessage)).ToList();
        Assert.Equal(24, gossip.Select(KeyOf).Distinct().Count());
        AssertEveryUpdateAfterItsAnnouncement(gossip);
        outbox.Complete();
    }

    private static string KeyOf(IMessage message) => message switch
    {
        ChannelAnnouncementMessage a => $"256:{a.Payload.ShortChannelId}",
        ChannelUpdateMessage u => $"258:{u.Payload.ShortChannelId}:{u.Payload.Direction}:{u.Payload.Timestamp}",
        NodeAnnouncementMessage n => $"257:{n.Payload.NodeId}:{n.Payload.Timestamp}",
        _ => message.Type.ToString()
    };

    private static void AssertEveryUpdateAfterItsAnnouncement(IReadOnlyList<IMessage> sent)
    {
        var announced = new HashSet<ShortChannelId>();
        foreach (var message in sent)
        {
            switch (message)
            {
                case ChannelAnnouncementMessage announcement:
                    Assert.True(announced.Add(announcement.Payload.ShortChannelId));
                    break;
                case ChannelUpdateMessage update:
                    Assert.Contains(update.Payload.ShortChannelId, announced);
                    break;
            }
        }
    }

    private FakeGossipPeer AddPeer(byte seed)
    {
        var peer = new FakeGossipPeer(seed);
        _peers.Add(new GossipPeer(peer.PeerPubKey, peer));
        _filters[peer] = new GossipTimestampFilter(0, uint.MaxValue);
        return peer;
    }

    private void RaiseFilter(FakeGossipPeer peer) =>
        _syncManager.Raise(m => m.FilterReceived += null, new GossipFilterReceivedEventArgs(peer, _filters[peer]));

    /// <summary>A collect tick 10 s in, then the tick at the end of one flush interval.</summary>
    private async Task FlushAllAsync()
    {
        await TickAsync(TimeSpan.FromSeconds(10));
        await TickAsync(s_flushInterval);
    }

    private Task<int> TickAsync(TimeSpan by)
    {
        _clock.Advance(by);
        return _relay.RelayTickAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A peer outbox with a gossip cap (NL-360) whose peer reads only when told to (<see cref="Drain"/>): what it
    /// drains goes to the fake peer in queue order.
    /// </summary>
    private sealed class CappedOutboxSender(int cap) : IGossipPeerSender
    {
        private readonly Dictionary<IPeerService, Queue<(IMessage Message, int Size)>> _queues =
            new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<IPeerService, long> _sent = new(ReferenceEqualityComparer.Instance);

        public ValueTask<GossipEnqueueResult> SendAsync(GossipPeer peer, IMessage message, int size)
        {
            lock (_queues)
            {
                var queue = QueueOf(peer.Service);
                if (queue.Count >= cap)
                    return ValueTask.FromResult(GossipEnqueueResult.Full);

                queue.Enqueue((message, size));
                return ValueTask.FromResult(GossipEnqueueResult.Queued);
            }
        }

        public GossipOutboxDepth? GetDepth(GossipPeer peer)
        {
            lock (_queues)
            {
                var queue = QueueOf(peer.Service);
                return new GossipOutboxDepth(queue.Count, queue.Sum(q => (long)q.Size), cap, 0,
                                             _sent.GetValueOrDefault(peer.Service));
            }
        }

        public int QueuedFor(FakeGossipPeer peer)
        {
            lock (_queues)
                return QueueOf(peer).Count;
        }

        public void Drain(FakeGossipPeer peer, int count)
        {
            List<IMessage> taken = [];
            lock (_queues)
            {
                var queue = QueueOf(peer);
                while (taken.Count < count && queue.TryDequeue(out var entry))
                    taken.Add(entry.Message);
                _sent[peer] = _sent.GetValueOrDefault(peer) + taken.Count;
            }

            foreach (var message in taken)
                peer.SendGossipMessageAsync(message).GetAwaiter().GetResult();
        }

        private Queue<(IMessage Message, int Size)> QueueOf(IPeerService service)
        {
            if (!_queues.TryGetValue(service, out var queue))
                _queues[service] = queue = new Queue<(IMessage Message, int Size)>();
            return queue;
        }
    }
}