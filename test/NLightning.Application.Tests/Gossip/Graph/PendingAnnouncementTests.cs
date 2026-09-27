using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Metrics;
using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Gossip.Validation;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Metrics;
using Relay;
using Sync;

/// <summary>
/// NL-406: a <c>channel_announcement</c> without any <c>channel_update</c> (BOLT 7: a node MUST NOT send one; Core
/// Lightning streams a quarter of the mainnet graph that way) waits outside the graph in the
/// <see cref="PendingAnnouncementIndex"/>: not in <see cref="IGraphView"/>, not served in query replies, not relayed,
/// not looked up on chain; its first valid update promotes it (chain check, then the graph); it expires after the TTL
/// and the index is capped.
/// </summary>
public class PendingAnnouncementTests : IDisposable
{
    private static readonly ShortChannelId s_scid = new(120, 1, 0);
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_aliceFunding = new(11);
    private static readonly TestGossipKey s_bobFunding = new(12);
    private static readonly TestGossipKey s_mallory = new(66);
    private static readonly TestGossipKey s_malloryTwo = new(67);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    private readonly GossipMetrics _metrics = new();
    private readonly GossipMetricsRecorder _recorder;

    public PendingAnnouncementTests()
    {
        _recorder = new GossipMetricsRecorder(_metrics);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task Given_AnAnnouncementWithoutUpdate_When_Processed_Then_ItIsPendingOutsideTheGraphWithoutAChainLookup()
    {
        // Arrange
        var kit = CreateKit();

        // Act
        var result = await ProcessAsync(kit, GraphTestKit.CreatePeer().Object, Announcement());

        // Assert
        Assert.Equal(GossipIngressOutcome.Pending, result.Outcome);
        Assert.True(kit.Ingress.IsPendingAnnouncement(s_scid));
        Assert.Equal(1, kit.Ingress.PendingAnnouncementCount);
        Assert.False(kit.Store.GetSnapshot().TryGetChannel(s_scid, out _));
        Assert.Equal(0, kit.Store.ChannelCount);
        Assert.False(kit.Store.NodeHasChannels(s_alice.PubKey));
        kit.FundingLookup.VerifyNoOtherCalls();
        Assert.Equal(0, _recorder.Sum("nlightning.gossip.messages.accepted",
                                      (GossipMetrics.TypeTag, "channel_announcement")));
        Assert.Equal(1, _recorder.ObserveQueue("pending_announcements"));
    }

    [Fact]
    public async Task Given_APendingAnnouncement_When_Queried_Then_ItIsNeitherListedNorServed()
    {
        // Arrange (BOLT 7: a node MUST NOT send a channel_announcement without a channel_update, B7-Q-05)
        var kit = CreateKit();
        await ProcessAsync(kit, GraphTestKit.CreatePeer().Object, Announcement());
        var responder = new QueryResponder(kit.Store);

        // Act
        var ranges = responder.CreateRangeReplies(
            new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest, 0, 1_000)),
            ChainConstants.Regtest);
        var byId = responder.CreateShortChannelIdsReplies(
            new QueryShortChannelIdsMessage(new QueryShortChannelIdsPayload(
                                                ChainConstants.Regtest,
                                                GossipQueryCodec.EncodeShortChannelIds([s_scid]))),
            ChainConstants.Regtest, fullInformation: true);

        // Assert
        Assert.All(ranges, r => Assert.Empty(GossipQueryCodec.DecodeShortChannelIds(
                                                 r.Payload.EncodedShortIds.Span, "test")));
        Assert.IsType<ReplyShortChannelIdsEndMessage>(Assert.Single(byId));
    }

    [Fact]
    public async Task Given_APendingAnnouncement_When_TheRelayFlushes_Then_NothingGoesOutUntilItsFirstUpdatePromotesIt()
    {
        // Arrange: a peer with an open filter, the relay over the ingress's graph
        var kit = CreateKit();
        var peer = new FakeGossipPeer(0x41);
        using var relay = CreateRelay(kit, peer, out var clock);
        await relay.RelayTickAsync(TestContext.Current.CancellationToken);
        var origin = GraphTestKit.CreatePeer().Object;
        await ProcessAsync(kit, origin, Announcement());

        // Act
        await FlushAsync(relay, clock);
        var whilePending = peer.Sent.ToList();
        await ProcessAsync(kit, origin, AliceUpdate(s_now - 10));
        await FlushAsync(relay, clock);

        // Assert
        Assert.Empty(whilePending);
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate], peer.Sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Given_APendingAnnouncement_When_ItsFirstValidUpdateArrives_Then_ItIsLookedUpPromotedAndTheUpdateApplied()
    {
        // Arrange
        var kit = CreateKit(amountSat: 2_000_000);
        var peer = GraphTestKit.CreatePeer().Object;
        await ProcessAsync(kit, peer, Announcement());

        // Act
        var result = await ProcessAsync(kit, peer, AliceUpdate(s_now - 10));

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.False(kit.Ingress.IsPendingAnnouncement(s_scid));
        Assert.True(kit.Store.GetSnapshot().TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphChannelVerification.Verified, channel.Verification);
        Assert.Equal(2_000_000UL, channel.CapacitySat);
        Assert.NotNull(channel.GetPolicy(GraphTestKit.DirectionOf(s_alice, s_bob)));
        Assert.True(channel.RawAnnouncement.Span.SequenceEqual(Announcement().Payload.GetBytes()));
        Assert.True(kit.Store.TryGetFundingTxId(s_scid, out var txId));
        Assert.Equal(GraphTestKit.TxIdFor(s_scid), txId);
        kit.FundingLookup.Verify(l => l.VerifyAsync(s_scid, It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                    It.IsAny<LightningMoney?>(), It.IsAny<CancellationToken>()),
                                 Times.Once);
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.accepted",
                                      (GossipMetrics.TypeTag, "channel_announcement")));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.accepted",
                                      (GossipMetrics.TypeTag, "channel_update")));
        Assert.Equal(0, _recorder.ObserveQueue("pending_announcements"));
    }

    [Fact]
    public async Task Given_AnUpdateBeforeItsAnnouncement_When_TheAnnouncementArrives_Then_ItIsPromotedAtOnce()
    {
        // Arrange
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer().Object;
        Assert.Equal(GossipIngressOutcome.Orphaned, (await ProcessAsync(kit, peer, AliceUpdate(s_now - 10))).Outcome);

        // Act
        var result = await ProcessAsync(kit, peer, Announcement());

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.Equal(0, kit.Ingress.PendingAnnouncementCount);
        Assert.Equal(0, kit.Ingress.Orphans.Count);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.NotNull(channel.GetPolicy(GraphTestKit.DirectionOf(s_alice, s_bob)));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.accepted",
                                      (GossipMetrics.TypeTag, "channel_announcement")));
    }

    [Fact]
    public async Task Given_APendingAnnouncement_When_TheTtlPasses_Then_ItExpiresAndALaterUpdateWaitsAsAnOrphan()
    {
        // Arrange: the default TTL of 14 days
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer().Object;
        await ProcessAsync(kit, peer, Announcement());

        // Act
        kit.Clock.Now = GraphTestKit.DefaultNow + TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1);
        var prunedBefore = kit.Ingress.PrunePendingAnnouncements();
        var pendingBefore = kit.Ingress.IsPendingAnnouncement(s_scid);
        kit.Clock.Now = GraphTestKit.DefaultNow + TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1);
        var pendingAfter = kit.Ingress.IsPendingAnnouncement(s_scid);
        var pruned = kit.Ingress.PrunePendingAnnouncements();
        var update = await ProcessAsync(kit, peer, AliceUpdate((uint)kit.Clock.Now.ToUnixTimeSeconds() - 10));

        // Assert
        Assert.Equal(0, prunedBefore);
        Assert.True(pendingBefore);
        Assert.False(pendingAfter);
        Assert.Equal(1, pruned);
        Assert.Equal(0, kit.Ingress.PendingAnnouncementCount);
        Assert.Equal(GossipIngressOutcome.Orphaned, update.Outcome);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.dropped",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.PendingExpired)));
        kit.FundingLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AFullPendingIndex_When_AnotherAnnouncementArrives_Then_TheOldestMakesRoom()
    {
        // Arrange: room for two
        var kit = CreateKit(configure: o => o.MaxPendingAnnouncements = 2);
        var peer = GraphTestKit.CreatePeer().Object;
        var scids = new[] { new ShortChannelId(130, 1, 0), new ShortChannelId(131, 1, 0), new ShortChannelId(132, 1, 0) };

        // Act
        foreach (var scid in scids)
        {
            Assert.Equal(GossipIngressOutcome.Pending, (await ProcessAsync(kit, peer, Announcement(scid))).Outcome);
            kit.Clock.Now += TimeSpan.FromSeconds(1);
        }

        var oldestUpdate = await ProcessAsync(kit, peer, AliceUpdate(s_now, scids[0]));
        var newestUpdate = await ProcessAsync(kit, peer, AliceUpdate(s_now, scids[2]));

        // Assert
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.dropped",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.PendingFull)));
        Assert.False(kit.Ingress.IsPendingAnnouncement(scids[0]));
        Assert.Equal(GossipIngressOutcome.Orphaned, oldestUpdate.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, newestUpdate.Outcome);
        Assert.True(kit.Ingress.IsPendingAnnouncement(scids[1]));
        Assert.Equal(1, kit.Ingress.PendingAnnouncementCount);
    }

    [Fact]
    public async Task Given_AForgedPendingAnnouncement_When_TheRealUpdateArrives_Then_NobodyIsWarnedAndTheRealAnnouncementWins()
    {
        // Arrange: mallory announces alice's scid with nodes and funding keys of her own (it was never checked on
        // chain, so an update that does not match it proves nothing against the sender)
        var kit = CreateKit();
        var mallory = GraphTestKit.CreatePeer(0x55);
        var honest = GraphTestKit.CreatePeer(0x56);
        await ProcessAsync(kit, mallory.Object,
                           GraphTestKit.SignedChannelAnnouncement(s_scid, s_mallory, s_malloryTwo,
                                                                  new TestGossipKey(76), new TestGossipKey(77)));

        // Act
        var update = await ProcessAsync(kit, honest.Object, AliceUpdate(s_now - 10));
        var real = await ProcessAsync(kit, honest.Object, Announcement());

        // Assert: the update waited; the real announcement replaced the forgery and was promoted by it
        Assert.Equal(GossipIngressOutcome.Orphaned, update.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, real.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Contains(s_alice.PubKey, new[] { channel.NodeId1, channel.NodeId2 });
        foreach (var peer in new[] { mallory, honest })
        {
            peer.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);
            peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        }

        Assert.Equal(0, kit.Ingress.Misbehaviour.GetScore(honest.Object.PeerPubKey));
    }

    [Fact]
    public async Task Given_ARealPendingAnnouncement_When_AForgeryFollowsBeforeTheRealUpdate_Then_TheRealChannelIsPromoted()
    {
        // Arrange: the real announcement waits, then mallory announces the same scid with her own nodes and keys (the
        // regression: the forgery used to replace the real one, so the honest update was orphaned)
        var kit = CreateKit();
        var mallory = GraphTestKit.CreatePeer(0x55);
        var honest = GraphTestKit.CreatePeer(0x56);
        Assert.Equal(GossipIngressOutcome.Pending, (await ProcessAsync(kit, honest.Object, Announcement())).Outcome);
        var forgery = await ProcessAsync(kit, mallory.Object,
                                         GraphTestKit.SignedChannelAnnouncement(s_scid, s_mallory, s_malloryTwo,
                                                                                new TestGossipKey(76),
                                                                                new TestGossipKey(77)));

        // Act
        var update = await ProcessAsync(kit, honest.Object, AliceUpdate(s_now - 10));

        // Assert
        Assert.Equal(GossipIngressOutcome.Pending, forgery.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, update.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Contains(s_alice.PubKey, new[] { channel.NodeId1, channel.NodeId2 });
        Assert.False(kit.Ingress.IsPendingAnnouncement(s_scid));
        Assert.Equal(0, kit.Ingress.PendingAnnouncementCount);
        foreach (var peer in new[] { mallory, honest })
            peer.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);
    }

    [Fact]
    public async Task Given_OnlyAForgedPendingAnnouncement_When_TheRealUpdateArrives_Then_ItWaitsAndTheScidIsAskedForAgain()
    {
        // Arrange
        var kit = CreateKit();
        var mallory = GraphTestKit.CreatePeer(0x55);
        var honest = GraphTestKit.CreatePeer(0x56);
        await ProcessAsync(kit, mallory.Object,
                           GraphTestKit.SignedChannelAnnouncement(s_scid, s_mallory, s_malloryTwo,
                                                                  new TestGossipKey(76), new TestGossipKey(77)));

        // Act
        var update = await ProcessAsync(kit, honest.Object, AliceUpdate(s_now - 10));

        // Assert: nobody blamed, and the sync asks for the real announcement again
        Assert.Equal(GossipIngressOutcome.Orphaned, update.Outcome);
        Assert.Contains(s_scid, kit.Ingress.TakeMissedShortChannelIds());
        honest.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);
        Assert.Equal(0, kit.Ingress.Misbehaviour.GetScore(honest.Object.PeerPubKey));
    }

    [Fact]
    public async Task Given_APeerFillingThePendingIndex_When_AnotherPeersAnnouncementArrives_Then_TheFloodersOldestMakesRoom()
    {
        // Arrange: the honest announcement came first, then mallory fills the index with forgeries of her own
        var kit = CreateKit(configure: o => o.MaxPendingAnnouncements = 3);
        var mallory = GraphTestKit.CreatePeer(0x55);
        var honest = GraphTestKit.CreatePeer(0x56);
        await ProcessAsync(kit, honest.Object, Announcement());
        var forged = new[] { new ShortChannelId(140, 1, 0), new ShortChannelId(141, 1, 0) };
        foreach (var scid in forged)
        {
            kit.Clock.Now += TimeSpan.FromSeconds(1);
            await ProcessAsync(kit, mallory.Object,
                               GraphTestKit.SignedChannelAnnouncement(scid, s_mallory, s_malloryTwo,
                                                                      new TestGossipKey(76), new TestGossipKey(77)));
        }

        // Act
        kit.Clock.Now += TimeSpan.FromSeconds(1);
        var another = new ShortChannelId(121, 1, 0);
        await ProcessAsync(kit, honest.Object, Announcement(another));

        // Assert: the honest entries stay, mallory's oldest went
        Assert.True(kit.Ingress.IsPendingAnnouncement(s_scid));
        Assert.True(kit.Ingress.IsPendingAnnouncement(another));
        Assert.False(kit.Ingress.IsPendingAnnouncement(forged[0]));
        Assert.True(kit.Ingress.IsPendingAnnouncement(forged[1]));
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await ProcessAsync(kit, honest.Object, AliceUpdate(s_now - 10))).Outcome);
    }

    [Fact]
    public async Task Given_APendingAnnouncementTheChainContradicts_When_AnotherPeersUpdatePromotesIt_Then_OnlyTheAnnouncerIsScored()
    {
        // Arrange: one contradiction bans; the announcer's connection is not the update's
        var kit = CreateKit(configure: o => o.MisbehaviourThreshold = 1);
        kit.FundingFails(FundingOutputStatus.ScriptMismatch);
        var announcer = GraphTestKit.CreatePeer(0x55);
        var relayer = GraphTestKit.CreatePeer(0x56);
        await ProcessAsync(kit, announcer.Object, Announcement());

        // Act
        var result = await ProcessAsync(kit, relayer.Object, AliceUpdate(s_now - 10));

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(GossipMetricReasons.ChainMismatch, result.LimitReason);
        Assert.False(kit.Ingress.IsPendingAnnouncement(s_scid));
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.True(kit.Ingress.IsBannedForMisbehaviour(announcer.Object.PeerPubKey));
        Assert.False(kit.Ingress.IsBannedForMisbehaviour(relayer.Object.PeerPubKey));
        relayer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.TypeTag, "channel_announcement"),
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.ChainMismatch)));

        // The same announcement again is dropped as a known bad one, without a lookup
        Assert.Equal(GossipIngressOutcome.Ignored,
                     (await ProcessAsync(kit, relayer.Object, Announcement())).Outcome);
        kit.FundingLookup.Verify(l => l.VerifyAsync(s_scid, It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                    It.IsAny<LightningMoney?>(), It.IsAny<CancellationToken>()),
                                 Times.Once);
    }

    [Fact]
    public async Task Given_APendingAnnouncement_When_OurOwnChannelIsAnnounced_Then_ItIsStoredAsOwnAndLeavesTheIndex()
    {
        // Arrange
        var kit = CreateKit();
        await ProcessAsync(kit, GraphTestKit.CreatePeer().Object, Announcement());

        // Act
        await kit.Ingress.ApplyOwnAsync(Announcement(), LightningMoney.Satoshis(500_000),
                                        TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, kit.Ingress.PendingAnnouncementCount);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphChannelVerification.Own, channel.Verification);
    }

    [Fact]
    public async Task Given_ADisabledOrStaleFirstUpdate_When_Processed_Then_OnlyAValidUpdatePromotes()
    {
        // Arrange: an update older than two weeks is ignored (BOLT 7 stale), so it promotes nothing
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer().Object;
        await ProcessAsync(kit, peer, Announcement());

        // Act
        var stale = await ProcessAsync(kit, peer, AliceUpdate(s_now - 15 * 86_400));
        var stillPending = kit.Ingress.IsPendingAnnouncement(s_scid);
        var fresh = await ProcessAsync(kit, peer, AliceUpdate(s_now - 10));

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, stale.Outcome);
        Assert.Equal(GossipRejectReason.StaleUpdate, stale.RejectReason);
        Assert.True(stillPending);
        Assert.Equal(GossipIngressOutcome.Accepted, fresh.Outcome);
    }

    private GraphTestKit CreateKit(long amountSat = 1_000_000, Action<GossipGraphOptions>? configure = null)
    {
        var kit = new GraphTestKit(configure: configure, metrics: _metrics);
        kit.FundingFound(amountSat);
        return kit;
    }

    private static GossipRelayScheduler CreateRelay(GraphTestKit kit, FakeGossipPeer peer, out RelayTestClock clock)
    {
        clock = new RelayTestClock();
        var syncManager = new Mock<IGossipSyncManager>();
        var filter = new GossipTimestampFilter(0, uint.MaxValue);
        syncManager.Setup(m => m.TryGetPeerFilter(peer, out filter)).Returns(true);
        var directory = new Mock<IGossipPeerDirectory>();
        directory.Setup(d => d.GetConnectedPeers()).Returns([new GossipPeer(peer.PeerPubKey, peer)]);
        var options = new GossipRelayOptions
        {
            RelayFlushInterval = TimeSpan.FromSeconds(60),
            RelayCollectInterval = TimeSpan.FromSeconds(10),
            RelayTickInterval = TimeSpan.FromSeconds(1)
        };
        return new GossipRelayScheduler(directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                        Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                        Options.Create(new GossipOptions()), clock, null, kit.Store,
                                        syncManager.Object, new GossipOriginTracker(), Options.Create(options));
    }

    private static async Task FlushAsync(GossipRelayScheduler relay, RelayTestClock clock)
    {
        clock.Advance(TimeSpan.FromSeconds(10));
        await relay.RelayTickAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(60));
        await relay.RelayTickAsync(TestContext.Current.CancellationToken);
    }

    private static ChannelAnnouncementMessage Announcement(ShortChannelId? scid = null) =>
        GraphTestKit.SignedChannelAnnouncement(scid ?? s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding);

    private static ChannelUpdateMessage AliceUpdate(uint timestamp, ShortChannelId? scid = null) =>
        GraphTestKit.SignedChannelUpdate(scid ?? s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob), timestamp);

    private static Task<GossipIngressResult> ProcessAsync(GraphTestKit kit, IPeerService peer,
                                                          Domain.Protocol.Interfaces.IMessage message) =>
        kit.Ingress.ProcessAsync(peer, message, 0, TestContext.Current.CancellationToken);
}