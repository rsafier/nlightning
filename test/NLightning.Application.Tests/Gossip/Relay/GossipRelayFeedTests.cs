using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Graph;
using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Graph;
using Sync;

/// <summary>
/// NL-366: the relay's collect is fed from the ingress's accepted gossip, not from a diff of the whole graph snapshot:
/// what the ingress accepted between two collects goes out (newest version per slot), what only the graph holds (the
/// startup load, a test writing the store) never does, and a feed overflow falls back to one full pass, which finds
/// everything.
/// </summary>
public class GossipRelayFeedTests : IDisposable
{
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromSeconds(60);
    private static readonly ShortChannelId s_scid1 = new(500, 1, 0);
    private static readonly ShortChannelId s_scid2 = new(501, 1, 0);
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_bitcoinA = new(11);
    private static readonly TestGossipKey s_bitcoinB = new(12);
    private static readonly uint s_t1 = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    private readonly GraphTestKit _kit;
    private readonly RelayTestClock _clock = new();
    private readonly List<GossipPeer> _peers = [];
    private readonly Dictionary<IPeerService, GossipTimestampFilter> _filters = new(ReferenceEqualityComparer.Instance);
    private readonly Mock<IGossipSyncManager> _syncManager = new();
    private readonly GossipRelayScheduler _relay;

    public GossipRelayFeedTests()
    {
        _kit = new GraphTestKit();
        _kit.FundingFound();
        _syncManager.Setup(m => m.TryGetPeerFilter(It.IsAny<IPeerService>(), out It.Ref<GossipTimestampFilter>.IsAny))
                    .Returns(new TryGetFilter((IPeerService peer, out GossipTimestampFilter filter) =>
                                                  _filters.TryGetValue(peer, out filter)));
        var directory = new Mock<IGossipPeerDirectory>();
        directory.Setup(d => d.GetConnectedPeers()).Returns(() => _peers.ToList());
        var options = new GossipRelayOptions
        {
            RelayFlushInterval = s_flushInterval,
            RelayCollectInterval = TimeSpan.FromSeconds(10),
            RelayTickInterval = TimeSpan.FromSeconds(1)
        };
        _relay = new GossipRelayScheduler(directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                          Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                          Options.Create(new GossipOptions()), _clock, graphStore: _kit.Store,
                                          syncManager: _syncManager.Object, relayOptions: Options.Create(options),
                                          acceptedFeed: _kit.Ingress);
    }

    private delegate bool TryGetFilter(IPeerService peer, out GossipTimestampFilter filter);

    [Fact]
    public async Task Given_GossipAcceptedBetweenTwoCollects_When_Flushed_Then_TheAnnouncementGoesWithItsUpdate()
    {
        // Arrange (B7-RL-01: nothing of others before the peer's gossip_timestamp_filter)
        var peer = AddPeer(new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();

        // Act: accepted through the ingress, so both slots are in the feed
        var announcement = GraphTestKit.SignedChannelAnnouncement(s_scid1, s_alice, s_bob, s_bitcoinA, s_bitcoinB);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await _kit.AnnounceAsync(null, announcement, updateTimestamp: s_t1,
                                               cancellationToken: TestContext.Current.CancellationToken)).Outcome);
        await FlushAllAsync();

        // Assert
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate],
                     peer.Sent.Select(m => m.Type));
        var update = Assert.IsType<ChannelUpdateMessage>(peer.Sent[1]);
        Assert.Equal(s_scid1, update.Payload.ShortChannelId);
        Assert.Equal(s_t1, update.Payload.Timestamp);
    }

    [Fact]
    public async Task Given_AChangeOnlyTheGraphHolds_When_CollectedAndFlushed_Then_NothingIsRelayed()
    {
        // Arrange: the startup load (and any store write that is not an acceptance) is not in the feed
        var peer = AddPeer(new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();
        AddStoredChannel(s_scid1, s_t1);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public async Task Given_GossipAcceptedBeforeTheFirstCollect_When_Flushed_Then_TheBaselineIsNotRelayed()
    {
        // Arrange (a restart does not relay the stored graph): accepted before the relay's first collect
        var peer = AddPeer(new GossipTimestampFilter(0, uint.MaxValue));
        var announcement = GraphTestKit.SignedChannelAnnouncement(s_scid1, s_alice, s_bob, s_bitcoinA, s_bitcoinB);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await _kit.AnnounceAsync(null, announcement, updateTimestamp: s_t1,
                                               cancellationToken: TestContext.Current.CancellationToken)).Outcome);

        // Act: the first collect records the graph as it is
        await BaselineAsync();
        await FlushAllAsync();

        // Assert
        Assert.Empty(peer.Sent);

        // Act: the next acceptance after the baseline is relayed
        var second = GraphTestKit.SignedChannelAnnouncement(s_scid2, s_alice, s_bob, s_bitcoinA, s_bitcoinB);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await _kit.AnnounceAsync(null, second, updateTimestamp: s_t1 + 5,
                                               cancellationToken: TestContext.Current.CancellationToken)).Outcome);
        await FlushAllAsync();

        // Assert: only the new channel (the first was seen at the baseline)
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate],
                     peer.Sent.Select(m => m.Type));
        Assert.Equal(s_scid2, Assert.IsType<ChannelAnnouncementMessage>(peer.Sent[0]).Payload.ShortChannelId);
    }

    [Fact]
    public async Task Given_TwoVersionsOfOneSlotAccepted_When_Collected_Then_OnlyTheNewestGoesOut()
    {
        // Arrange: the first version is recorded by the baseline
        var peer = AddPeer(new GossipTimestampFilter(0, uint.MaxValue));
        var announcement = GraphTestKit.SignedChannelAnnouncement(s_scid1, s_alice, s_bob, s_bitcoinA, s_bitcoinB);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await _kit.AnnounceAsync(null, announcement, updateTimestamp: s_t1,
                                               cancellationToken: TestContext.Current.CancellationToken)).Outcome);
        await BaselineAsync();

        // Act: a newer update of the same direction (a different fee, so not a keep-alive) and a collect
        var newer = GraphTestKit.SignedChannelUpdate(s_scid1, s_alice,
                                                     GraphTestKit.DirectionOf(s_alice, s_bob), s_t1 + 100,
                                                     feeBaseMsat: 2_000);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await _kit.Ingress.ProcessAsync(null, newer,
                                                      cancellationToken: TestContext.Current.CancellationToken)).Outcome);
        await FlushAllAsync();

        // Assert: one update, the newest, and (this connection never got it) its announcement with it (NL-368)
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate],
                     peer.Sent.Select(m => m.Type));
        var update = Assert.Single(peer.Sent.OfType<ChannelUpdateMessage>());
        Assert.Equal(s_t1 + 100, update.Payload.Timestamp);
    }

    [Fact]
    public async Task Given_TheFeedDroppedSlotsForItsBound_When_Collected_Then_AFullPassFindsEverything()
    {
        // Arrange: the feed holds 2 slots and both channels were accepted after the baseline
        _kit.Ingress.AcceptedCapacity = 2;
        var peer = AddPeer(new GossipTimestampFilter(0, uint.MaxValue));
        await BaselineAsync();
        foreach (var scid in (ShortChannelId[])[s_scid1, s_scid2])
        {
            var announcement = GraphTestKit.SignedChannelAnnouncement(scid, s_alice, s_bob, s_bitcoinA, s_bitcoinB);
            Assert.Equal(GossipIngressOutcome.Accepted,
                         (await _kit.AnnounceAsync(null, announcement, updateTimestamp: s_t1,
                                                   cancellationToken: TestContext.Current.CancellationToken)).Outcome);
        }

        // Act: the second channel pushed the first out of the feed; the collect falls back to the snapshot
        await FlushAllAsync();

        // Assert: nothing was lost (two announcements, two updates)
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate,
                         MessageTypes.ChannelUpdate], peer.Sent.Select(m => m.Type));
        Assert.Equal([s_scid1, s_scid2],
                     peer.Sent.OfType<ChannelAnnouncementMessage>().Select(m => m.Payload.ShortChannelId));
    }

    public void Dispose()
    {
        _relay.Dispose();
        _kit.Ingress.Dispose();
    }

    private FakeGossipPeer AddPeer(GossipTimestampFilter filter)
    {
        var peer = new FakeGossipPeer(0x41);
        _peers.Add(new GossipPeer(peer.PeerPubKey, peer));
        _filters[peer] = filter;
        return peer;
    }

    /// <summary>The first tick: records what the graph and the feed hold as they are.</summary>
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

    /// <summary>Writes a signed channel into the store the way the startup load would (not through the ingress).</summary>
    private void AddStoredChannel(ShortChannelId shortChannelId, uint timestamp)
    {
        var announcement = GraphTestKit.SignedChannelAnnouncement(shortChannelId, s_alice, s_bob,
                                                                  s_bitcoinA, s_bitcoinB).Payload;
        var channel = new GraphChannel(shortChannelId, announcement.NodeId1, announcement.NodeId2,
                                       announcement.BitcoinKey1, announcement.BitcoinKey2, 1_000_000,
                                       verification: GraphChannelVerification.Verified)
        {
            RawAnnouncement = announcement.GetBytes()
        };
        Assert.True(_kit.Store.TryAddChannel(channel));
        foreach (var (node, direction) in new[] { (s_alice, (byte)0), (s_bob, (byte)1) })
        {
            var update = GraphTestKit.SignedChannelUpdate(shortChannelId, node, direction, timestamp).Payload;
            Assert.True(_kit.Store.TryApplyPolicy(shortChannelId, GraphPolicy.FromChannelUpdate(update) with
            {
                RawUpdate = update.GetBytes()
            }));
        }
    }
}