using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Sync;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Queries;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// NL-415: the per-batch re-diff of a range sync also skips the unknown channels whose announcement is already on its
/// way (asked from another sync peer, or pending in the ingress or the funding output lookup); NL-414: the
/// missed-channel retry leaves a pending channel (a funding output spent in the mempool) for a later round.
/// </summary>
public class GossipSyncPendingChannelsTests : IDisposable
{
    private const uint Tip = 500;
    private static readonly ChainHash s_chain = ChainConstants.Regtest;

    private readonly SyncTestGraph _graph = new();
    private readonly Mock<IGossipIngress> _ingress = new();
    private readonly List<GossipSyncManager> _managers = [];
    private readonly FakePendingChannels _pending = new();
    private List<ShortChannelId> _missed = [];

    public GossipSyncPendingChannelsTests()
    {
        _ingress.SetupGet(i => i.IsEnabled).Returns(true);
    }

    private static ShortChannelId[] Scids(int count) =>
        Enumerable.Range(0, count).Select(i => new ShortChannelId(100 + (uint)i, 0, 0)).ToArray();

    [Fact]
    public async Task Given_TwoSyncPeers_When_TheirBatchesInterleave_Then_EachUnknownChannelIsAskedFromOnePeer()
    {
        // Arrange: batches of 2, both peers list the same 5 unknown channels
        var manager = CreateManager(o =>
        {
            o.MaxScidsPerQuery = 2;
            o.SyncPeers = 2;
        });
        var ids = Scids(5);
        var peerA = new FakeGossipPeer(1);
        var peerB = new FakeGossipPeer(2);
        manager.OnPeerInitialized(peerA);
        manager.OnPeerInitialized(peerB);
        await peerA.NextAsync<QueryChannelRangeMessage>();
        await peerB.NextAsync<QueryChannelRangeMessage>();

        // Act: A's first batch is out and unanswered (its answers wait for their chain lookups) when B diffs
        manager.HandleMessage(peerA, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        var a1 = Ids(await peerA.NextAsync<QueryShortChannelIdsMessage>());
        manager.HandleMessage(peerB, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        var b1 = Ids(await peerB.NextAsync<QueryShortChannelIdsMessage>());
        manager.HandleMessage(peerA, End());
        var a2 = Ids(await peerA.NextAsync<QueryShortChannelIdsMessage>());
        manager.HandleMessage(peerB, End());
        await peerB.NextAsync<GossipTimestampFilterMessage>();
        manager.HandleMessage(peerA, End());
        await peerA.NextAsync<GossipTimestampFilterMessage>();

        // Assert: every channel asked for exactly once over both peers
        Assert.Equal(ids[..2], a1);
        Assert.Equal(ids[2..4], b1);
        Assert.Equal(ids[4..], a2);
        Assert.Equal(ids, a1.Concat(b1).Concat(a2).OrderBy(s => s.BlockHeight));
    }

    [Fact]
    public async Task Given_QueriedChannelTtlZero_When_TwoPeersSync_Then_BothAskForTheSameChannels()
    {
        // Arrange: the NL-402 behavior alone (the graph is all the re-diff sees)
        var manager = CreateManager(o =>
        {
            o.MaxScidsPerQuery = 2;
            o.SyncPeers = 2;
            o.QueriedChannelTtl = TimeSpan.Zero;
        });
        var ids = Scids(2);
        var peerA = new FakeGossipPeer(1);
        var peerB = new FakeGossipPeer(2);
        manager.OnPeerInitialized(peerA);
        manager.OnPeerInitialized(peerB);
        await peerA.NextAsync<QueryChannelRangeMessage>();
        await peerB.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peerA, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        var a1 = Ids(await peerA.NextAsync<QueryShortChannelIdsMessage>());
        manager.HandleMessage(peerB, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        var b1 = Ids(await peerB.NextAsync<QueryShortChannelIdsMessage>());

        // Assert
        Assert.Equal(ids, a1);
        Assert.Equal(ids, b1);
        Assert.Equal(0, manager.QueriedChannels.Count);
    }

    [Fact]
    public async Task Given_APeerWhoseQueryBrokeTheRules_When_AnotherPeerSyncs_Then_ItsChannelsAreAskedFromTheOther()
    {
        // Arrange: A claims two channels, then answers with another chain's reply_short_channel_ids_end
        var manager = CreateManager(o =>
        {
            o.MaxScidsPerQuery = 2;
            o.SyncPeers = 2;
        });
        var ids = Scids(2);
        var peerA = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peerA);
        await peerA.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(peerA, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        Assert.Equal(ids, Ids(await peerA.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peerA, new ReplyShortChannelIdsEndMessage(
                                  new ReplyShortChannelIdsEndPayload(ChainConstants.Main, true)));
        await manager.WhenIdleAsync(peerA, TestContext.Current.CancellationToken);
        Assert.Single(peerA.Warnings);

        // Act
        var peerB = new FakeGossipPeer(2);
        manager.OnPeerInitialized(peerB);
        await peerB.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(peerB, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        var b1 = Ids(await peerB.NextAsync<QueryShortChannelIdsMessage>());

        // Assert: A's claims were released with its failed query
        Assert.Equal(ids, b1);
    }

    [Fact]
    public async Task Given_ChannelsPendingInTheGraphPipeline_When_TheBatchIsBuilt_Then_TheyAreNotAskedFor()
    {
        // Arrange: ids[1] waits in the ingress queue, ids[3] for its chain lookup (another peer's announcements)
        var manager = CreateManager(o => o.MaxScidsPerQuery = 8);
        var ids = Scids(5);
        _pending.Add(ids[1]);
        _pending.Add(ids[3]);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));
        var query = Ids(await peer.NextAsync<QueryShortChannelIdsMessage>());

        // Assert
        Assert.Equal([ids[0], ids[2], ids[4]], query);
    }

    [Fact]
    public async Task Given_APendingSourceThatThrows_When_TheBatchIsBuilt_Then_TheChannelIsAskedFor()
    {
        // Arrange
        var failing = new Mock<IGossipPendingChannels>();
        failing.Setup(p => p.IsPending(It.IsAny<ShortChannelId>())).Throws<InvalidOperationException>();
        var manager = CreateManager(pendingChannels: [failing.Object]);
        var ids = Scids(1);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<QueryChannelRangeMessage>();

        // Act
        manager.HandleMessage(peer, RangeReplyCollectorTests.Reply(0, Tip + 1, true, ids));

        // Assert
        Assert.Equal(ids, Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
    }

    [Fact]
    public async Task Given_AMissedChannelWhoseFundingIsSpentInTheMempool_When_Retried_Then_ItWaitsForTheNextBlock()
    {
        // Arrange: NL-414, the ingress gave up on ids[0] (its funding output is spent by a mempool transaction, so
        // the lookup keeps that answer until the next block) and dropped ids[1] at a full queue
        var ids = Scids(2);
        _missed = [.. ids];
        _pending.Add(ids[0]);
        var manager = CreateManager(o => o.SyncPeers = 0);
        var peer = new FakeGossipPeer(1);
        manager.OnPeerInitialized(peer);
        await peer.NextAsync<GossipTimestampFilterMessage>();

        // Act / Assert: only ids[1] now
        Assert.Equal(1, manager.RetryMissedShortChannelIds());
        Assert.Equal([ids[1]], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
        manager.HandleMessage(peer, End());
        await manager.WhenIdleAsync(peer, TestContext.Current.CancellationToken);

        // Still no block: nothing asked, ids[0] kept
        Assert.Equal(0, manager.RetryMissedShortChannelIds());

        // The next block ends the pending answer: asked once more
        _pending.Remove(ids[0]);
        Assert.Equal(1, manager.RetryMissedShortChannelIds());
        Assert.Equal([ids[0]], Ids(await peer.NextAsync<QueryShortChannelIdsMessage>()));
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.Dispose();
        GC.SuppressFinalize(this);
    }

    private GossipSyncManager CreateManager(Action<GossipSyncOptions>? configure = null,
                                            IEnumerable<IGossipPendingChannels>? pendingChannels = null)
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
                                            }, () => Tip,
                                            pendingChannels: pendingChannels ?? [_pending, _pending]);
        _managers.Add(manager);
        return manager;
    }

    private static ReplyShortChannelIdsEndMessage End() => new(new ReplyShortChannelIdsEndPayload(s_chain, true));

    private static ShortChannelId[] Ids(QueryShortChannelIdsMessage query) =>
        GossipQueryCodec.DecodeShortChannelIds(query.Payload.EncodedShortIds.Span, "test");

    /// <summary>A pending-channel view the test fills.</summary>
    private sealed class FakePendingChannels : IGossipPendingChannels
    {
        private readonly HashSet<ShortChannelId> _pending = [];
        private readonly Lock _gate = new();

        public void Add(ShortChannelId shortChannelId)
        {
            lock (_gate)
                _pending.Add(shortChannelId);
        }

        public void Remove(ShortChannelId shortChannelId)
        {
            lock (_gate)
                _pending.Remove(shortChannelId);
        }

        public bool IsPending(ShortChannelId shortChannelId)
        {
            lock (_gate)
                return _pending.Contains(shortChannelId);
        }
    }
}