using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Metrics;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Validation;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Gossip;
using Metrics;

/// <summary>
/// BOLT 7 plan §3.8, G5-T1/G5-T2: the ingress rate limits, keep-alive rule, future timestamps, graph size limits,
/// queue and orphan caps, and the misbehaviour score with its ban.
/// </summary>
public class GossipIngressLimitsTests : IDisposable
{
    private static readonly ShortChannelId s_scid = new(110, 1, 0);
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_carol = new(3);
    private static readonly TestGossipKey s_aliceFunding = new(11);
    private static readonly TestGossipKey s_bobFunding = new(12);
    private static readonly TestGossipKey s_carolFunding = new(13);
    private static readonly TestGossipKey s_mallory = new(66);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    private readonly GossipMetrics _metrics = new();
    private readonly GossipMetricsRecorder _recorder;

    public GossipIngressLimitsTests()
    {
        _recorder = new GossipMetricsRecorder(_metrics);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task Given_UpdatesOfOneDirection_When_MoreThanTheBurstComeWithinAMinute_Then_TheRateLimitsThemUntilAMinutePassed()
    {
        // Arrange (plan §3.8: one accepted channel_update per 60 s per channel direction, burst 4)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);

        // Act
        var results = new List<GossipIngressResult>();
        for (var i = 1; i <= 5; i++)
            results.Add(await ProcessAsync(kit, peer, Update(s_alice, direction, s_now - 1_000 + (uint)i, i)));
        var otherDirection = await ProcessAsync(kit, peer, Update(s_bob, (byte)(1 - direction), s_now - 900, 1));
        kit.Clock.Now += TimeSpan.FromSeconds(60);
        var afterAMinute = await ProcessAsync(kit, peer, Update(s_alice, direction, s_now - 800, 6));
        var rightAfter = await ProcessAsync(kit, peer, Update(s_alice, direction, s_now - 799, 7));

        // Assert
        Assert.All(results.Take(4), r => Assert.Equal(GossipIngressOutcome.Accepted, r.Outcome));
        Assert.Equal(GossipIngressOutcome.Ignored, results[4].Outcome);
        Assert.Equal(GossipMetricReasons.RateLimited, results[4].LimitReason);
        Assert.Equal(GossipIngressOutcome.Accepted, otherDirection.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, afterAMinute.Outcome);
        Assert.Equal(GossipMetricReasons.RateLimited, rightAfter.LimitReason);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(s_now - 800, channel.GetPolicy(direction)!.Timestamp);
        Assert.Equal(2, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.RateLimited)));
    }

    [Fact]
    public async Task Given_ARateLimitedUpdate_When_ItsSignatureIsInvalid_Then_NoTokenIsSpentOnIt()
    {
        // Arrange (the limit is checked before the signature, and only accepted updates spend a token)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);

        // Act: four forged updates, then four genuine ones
        for (var i = 1; i <= 4; i++)
            await ProcessAsync(kit, peer, Update(s_mallory, direction, s_now - 1_000 + (uint)i, i));
        var genuine = new List<GossipIngressResult>();
        for (var i = 1; i <= 4; i++)
            genuine.Add(await ProcessAsync(kit, peer, Update(s_alice, direction, s_now - 500 + (uint)i, 10 + i)));

        // Assert
        Assert.All(genuine, r => Assert.Equal(GossipIngressOutcome.Accepted, r.Outcome));
    }

    [Fact]
    public async Task Given_AStoredUpdate_When_AKeepAliveWithTheSameFieldsIsLessThanADayNewer_Then_ItIsIgnored()
    {
        // Arrange (plan §3.8: a keep-alive is accepted only when more than 24 h newer)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var first = s_now - 3 * 86_400;
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await ProcessAsync(kit, peer, Update(s_alice, direction, first, 1))).Outcome);

        // Act
        var anHourLater = await ProcessAsync(kit, peer, Update(s_alice, direction, first + 3_600, 1));
        var exactlyADayLater = await ProcessAsync(kit, peer, Update(s_alice, direction, first + 86_400, 1));
        var changedAnHourLater = await ProcessAsync(kit, peer, Update(s_alice, direction, first + 3_601, 2));
        var aDayAfterThat = await ProcessAsync(kit, peer, Update(s_alice, direction, first + 3_601 + 86_401, 2));

        // Assert
        Assert.Equal(GossipMetricReasons.KeepAliveTooSoon, anHourLater.LimitReason);
        Assert.Equal(GossipMetricReasons.KeepAliveTooSoon, exactlyADayLater.LimitReason);
        Assert.Equal(GossipIngressOutcome.Accepted, changedAnHourLater.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, aDayAfterThat.Outcome);
    }

    [Fact]
    public async Task Given_AnAnnouncedNode_When_ANewerAnnouncementComesWithinTenMinutes_Then_ItIsRateLimited()
    {
        // Arrange (plan §3.8: one node_announcement per node per 10 minutes)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now - 100)))
                    .Outcome);

        // Act
        kit.Clock.Now += TimeSpan.FromMinutes(9);
        var tooSoon = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, "new"));
        var otherNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_bob, s_now));
        kit.Clock.Now += TimeSpan.FromMinutes(1);
        var inTime = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now + 1, "newer"));

        // Assert
        Assert.Equal(GossipMetricReasons.RateLimited, tooSoon.LimitReason);
        Assert.Equal(GossipIngressOutcome.Accepted, otherNode.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, inTime.Outcome);
        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out var stored));
        Assert.Equal(s_now + 1, stored.Timestamp);
    }

    [Fact]
    public async Task Given_GossipMoreThanFourteenDaysAhead_When_Processed_Then_ItIsDropped()
    {
        // Arrange (plan §3.8: future timestamps beyond 14 days are dropped)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var fifteenDays = s_now + 15 * 86_400;
        var thirteenDays = s_now + 13 * 86_400;

        // Act
        var farNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, fifteenDays));
        var farUpdate = await ProcessAsync(kit, peer, Update(s_alice, direction, fifteenDays, 1));
        var nearNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, thirteenDays));
        var nearUpdate = await ProcessAsync(kit, peer, Update(s_alice, direction, thirteenDays, 1));

        // Assert
        Assert.Equal(GossipMetricReasons.FutureTimestamp, farNode.LimitReason);
        Assert.Equal(GossipRejectReason.TimestampTooFarInFuture, farUpdate.RejectReason);
        Assert.Equal(GossipIngressOutcome.Accepted, nearNode.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, nearUpdate.Outcome);
    }

    [Fact]
    public async Task Given_AGraphAtMaxChannels_When_ANewChannelIsAnnounced_Then_ItIsRefusedWithoutAChainLookup()
    {
        // Arrange (plan §3.8: MaxChannels; new channels beyond it are rejected, logged and counted)
        var kit = await CreateKitWithChannelAsync(o => o.MaxChannels = 1);
        var peer = GraphTestKit.CreatePeer();
        var other = new ShortChannelId(111, 1, 0);

        // Act
        var refused = await ProcessAsync(kit, peer,
                                         GraphTestKit.SignedChannelAnnouncement(other, s_bob, s_carol, s_bobFunding,
                                                                                s_carolFunding));
        var known = await kit.Ingress.ProcessAsync(peer.Object, Announcement(), 1,
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipMetricReasons.GraphFull, refused.LimitReason);
        Assert.False(kit.Store.TryGetChannel(other, out _));
        Assert.Equal(GossipRejectReason.AlreadyKnown, known.RejectReason);
        kit.FundingLookup.Verify(l => l.VerifyAsync(other, It.IsAny<Domain.Crypto.ValueObjects.CompactPubKey>(),
                                                    It.IsAny<Domain.Crypto.ValueObjects.CompactPubKey>(),
                                                    It.IsAny<Domain.Money.LightningMoney?>(),
                                                    It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.TypeTag, "channel_announcement"),
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.GraphFull)));
    }

    [Fact]
    public async Task Given_AGraphAtMaxNodes_When_ANewNodeAnnouncesItself_Then_OnlyKnownNodesAreUpdated()
    {
        // Arrange (plan §3.8: MaxNodes)
        var kit = await CreateKitWithChannelAsync(o => o.MaxNodes = 1);
        var peer = GraphTestKit.CreatePeer();
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now - 100)))
                    .Outcome);

        // Act
        var newNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_bob, s_now - 100));
        kit.Clock.Now += TimeSpan.FromMinutes(10);
        var knownNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, "again"));

        // Assert
        Assert.Equal(GossipMetricReasons.GraphFull, newNode.LimitReason);
        Assert.False(kit.Store.TryGetNode(s_bob.PubKey, out _));
        Assert.Equal(GossipIngressOutcome.Accepted, knownNode.Outcome);
    }

    [Fact]
    public async Task Given_AFullOrphanCache_When_AnotherOrphanArrives_Then_ItIsDroppedAndCounted()
    {
        // Arrange (plan §3.8: orphan channel_updates 10,000, here 1)
        var kit = new GraphTestKit(configure: o => o.MaxOrphans = 1, metrics: _metrics);
        var peer = GraphTestKit.CreatePeer();

        // Act
        var first = await ProcessAsync(kit, peer,
                                       GraphTestKit.SignedChannelUpdate(new ShortChannelId(200, 1, 0), s_alice, 0,
                                                                        s_now));
        var second = await ProcessAsync(kit, peer,
                                        GraphTestKit.SignedChannelUpdate(new ShortChannelId(201, 1, 0), s_alice, 0,
                                                                         s_now));

        // Assert
        Assert.Equal(GossipIngressOutcome.Orphaned, first.Outcome);
        Assert.Equal(GossipIngressOutcome.Orphaned, second.Outcome);
        Assert.Equal(1, kit.Ingress.Orphans.Count);
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.dropped",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.OrphanCacheFull)));
        Assert.Equal(2, _recorder.Sum("nlightning.gossip.messages.orphaned"));
        Assert.Equal(1, _recorder.ObserveQueue("orphans"));
    }

    [Fact]
    public void Given_QueueCaps_When_APeerFloods_Then_ItsExcessAndThenTheGlobalExcessAreDropped()
    {
        // Arrange (plan §3.8: 2,000 per peer and 20,000 in total, here 2 and 3); the workers never start (the graph
        // load never completes), so the queue only fills
        var store = new Mock<IGraphStore>();
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource().Task);
        var ingress = new GossipIngress(store.Object, new GossipSignatureVerifier(), Mock.Of<IFundingOutputLookup>(),
                                        Microsoft.Extensions.Options.Options.Create(new GossipGraphOptions
                                        {
                                            MaxQueuedPerPeer = 2,
                                            MaxQueued = 3,
                                            Workers = 1
                                        }),
                                        Microsoft.Extensions.Options.Options.Create(new NodeOptions
                                        {
                                            BitcoinNetwork = BitcoinNetwork.Regtest
                                        }), NullLogger<GossipIngress>.Instance, new SettableTimeProvider(
                                            GraphTestKit.DefaultNow), metrics: _metrics);
        var flooder = GraphTestKit.CreatePeer(0x61).Object;
        var other = GraphTestKit.CreatePeer(0x62).Object;
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, 0, s_now);

        // Act
        var flood = Enumerable.Range(0, 3).Select(_ => ingress.TryEnqueue(flooder, update)).ToList();
        var otherFirst = ingress.TryEnqueue(other, update);
        var otherSecond = ingress.TryEnqueue(other, update);

        // Assert
        Assert.Equal([true, true, false], flood);
        Assert.True(otherFirst);
        Assert.False(otherSecond);
        Assert.Equal(3, ingress.QueuedCount);
        Assert.Equal(5, _recorder.Sum("nlightning.gossip.messages.received"));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.dropped",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.PeerQueueFull)));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.dropped",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.QueueFull)));
        Assert.Equal(3, _recorder.ObserveQueue("ingress"));
        Assert.Equal([s_scid], ingress.TakeMissedShortChannelIds());
        ingress.Dispose();
    }

    [Fact]
    public async Task Given_APeer_When_ItSendsFiveInvalidSignaturesInTenMinutes_Then_ItIsWarnedDisconnectedAndBannedForAnHour()
    {
        // Arrange (plan §3.8: 5 invalid signatures or chain mismatches in 10 min → warning, disconnect, 1 h ban)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer(0x66);
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);

        // Act
        for (var i = 1; i <= 4; i++)
            await ProcessAsync(kit, peer, Update(s_mallory, direction, s_now - 100 + (uint)i, i));
        var bannedAfterFour = kit.Store.IsBanned(peer.Object.PeerPubKey);
        await ProcessAsync(kit, peer, Update(s_mallory, direction, s_now - 50, 5));

        // Assert
        Assert.False(bannedAfterFour);
        Assert.True(kit.Store.IsBanned(peer.Object.PeerPubKey));
        peer.Verify(p => p.Disconnect(It.Is<WarningException>(e => e.Message.Contains("Too much invalid gossip"))),
                    Times.Once);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        var ban = Assert.Single(kit.Repository.Bans.Values);
        Assert.Equal(peer.Object.PeerPubKey, ban.NodeId);
        Assert.Equal(GraphTestKit.DefaultNow + TimeSpan.FromHours(1), ban.Until);
        Assert.Equal(5, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.InvalidSignature)));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.peers.banned"));

        // The ban ends after an hour
        kit.Clock.Now += TimeSpan.FromHours(1);
        Assert.False(kit.Store.IsBanned(peer.Object.PeerPubKey));
    }

    [Fact]
    public async Task Given_FourInvalidSignatures_When_TheFifthComesAfterTheWindow_Then_ThePeerIsNotBanned()
    {
        // Arrange
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer(0x66);
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        for (var i = 1; i <= 4; i++)
            await ProcessAsync(kit, peer, Update(s_mallory, direction, s_now - 100 + (uint)i, i));

        // Act
        kit.Clock.Now += TimeSpan.FromMinutes(10);
        await ProcessAsync(kit, peer, Update(s_mallory, direction, s_now - 50, 5));

        // Assert
        Assert.False(kit.Store.IsBanned(peer.Object.PeerPubKey));
        Assert.Equal(1, kit.Ingress.Misbehaviour.GetScore(peer.Object.PeerPubKey));
    }

    [Theory]
    [InlineData(FundingOutputStatus.ScriptMismatch, true)]
    [InlineData(FundingOutputStatus.AmountMismatch, true)]
    [InlineData(FundingOutputStatus.TransactionIndexOutOfRange, true)]
    [InlineData(FundingOutputStatus.OutputSpentOrMissing, false)]
    public async Task Given_FiveAnnouncementsTheChainContradicts_When_Processed_Then_OnlyMismatchesBanThePeer(
        FundingOutputStatus status, bool banned)
    {
        // Arrange (a spent funding output is a closed channel, never the relaying peer's fault)
        var kit = new GraphTestKit(metrics: _metrics);
        kit.FundingFails(status);
        var peer = GraphTestKit.CreatePeer(0x66);

        // Act
        for (var i = 0; i < 5; i++)
            await ProcessAsync(kit, peer,
                               GraphTestKit.SignedChannelAnnouncement(new ShortChannelId(300 + (uint)i, 1, 0), s_alice,
                                                                      s_bob, s_aliceFunding, s_bobFunding));

        // Assert
        Assert.Equal(banned, kit.Store.IsBanned(peer.Object.PeerPubKey));
        Assert.Equal(5, _recorder.Sum("nlightning.gossip.chain.lookups",
                                      (GossipMetrics.StatusTag, GossipMetrics.TagValue(status))));
    }

    [Fact]
    public async Task Given_FiveBadEncodings_When_Processed_Then_ThePeerIsBanned()
    {
        // Arrange (plan §3.3: bad encodings count too; each gets a warning without a disconnection)
        var kit = new GraphTestKit(metrics: _metrics);
        var peer = GraphTestKit.CreatePeer(0x66);

        // Act
        for (var i = 0; i < 5; i++)
        {
            var ordered = GraphTestKit.SignedChannelAnnouncement(new ShortChannelId(300 + (uint)i, 1, 0), s_alice,
                                                                 s_bob, s_aliceFunding, s_bobFunding).Payload;
            await ProcessAsync(kit, peer, new ChannelAnnouncementMessage(
                                   new Domain.Protocol.Payloads.ChannelAnnouncementPayload(
                                       ordered.NodeSignature2, ordered.NodeSignature1, ordered.BitcoinSignature2,
                                       ordered.BitcoinSignature1, ordered.Features, ordered.ChainHash,
                                       ordered.ShortChannelId, ordered.NodeId2, ordered.NodeId1, ordered.BitcoinKey2,
                                       ordered.BitcoinKey1)));
        }

        // Assert
        Assert.True(kit.Store.IsBanned(peer.Object.PeerPubKey));
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Once);
    }

    [Fact]
    public async Task Given_APeerBannedForMisbehaviour_When_ItReconnectsAndSendsGossip_Then_ItIsDroppedUntilTheBanEnds()
    {
        // Arrange: banned by its score; the new connection is left alone (it may carry our channels)
        var kit = await CreateKitWithChannelAsync();
        var first = GraphTestKit.CreatePeer(0x66);
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        for (var i = 1; i <= 5; i++)
            await ProcessAsync(kit, first, Update(s_mallory, direction, s_now - 100 + (uint)i, i));
        var connection = GraphTestKit.CreatePeer(0x66);

        // Act
        var whileBanned = kit.Ingress.TryEnqueue(connection.Object, Update(s_alice, direction, s_now - 10, 50));
        var again = kit.Ingress.TryEnqueue(connection.Object, Update(s_alice, direction, s_now - 9, 51));
        kit.Clock.Now += TimeSpan.FromHours(1);
        var afterTheBan = kit.Ingress.TryEnqueue(connection.Object, Update(s_alice, direction, s_now - 8, 52));

        // Assert
        Assert.False(whileBanned);
        Assert.False(again);
        Assert.True(afterTheBan);
        connection.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        Assert.Equal(2, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.BannedPeer)));
        await kit.Ingress.StopAsync();
    }

    [Fact]
    public async Task Given_ANodeBlacklistedForAConflictingAnnouncement_When_ItRelaysOthersGossip_Then_ItIsStillTaken()
    {
        // Arrange (B7-CA-04 blacklists a node's own gossip, not the peer's relay of other nodes)
        var kit = new GraphTestKit(metrics: _metrics);
        kit.FundingFound();
        var connection = GraphTestKit.CreatePeer(0x66);
        kit.Store.Ban(connection.Object.PeerPubKey, "conflicting announcement", GraphTestKit.DefaultNow.AddDays(14));

        // Act
        var taken = kit.Ingress.TryEnqueue(connection.Object, Announcement());
        await WaitForAsync(() => kit.Store.TryGetChannel(s_scid, out _));
        await kit.Ingress.StopAsync();

        // Assert
        Assert.True(taken);
        Assert.True(kit.Store.TryGetChannel(s_scid, out _));
        connection.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_TheIngress_When_MessagesAreProcessed_Then_TheCountersFollowTheOutcomes()
    {
        // Arrange (G5-T4: received/accepted/rejected by reason, chain lookups)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);

        // Act
        await ProcessAsync(kit, peer, Update(s_alice, direction, s_now - 10, 1));
        await ProcessAsync(kit, peer, Update(s_alice, direction, s_now - 10, 1));
        await ProcessAsync(kit, peer, Update(s_mallory, direction, s_now - 5, 2));

        // Assert
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.accepted",
                                      (GossipMetrics.TypeTag, "channel_announcement")));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.accepted",
                                      (GossipMetrics.TypeTag, "channel_update")));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.TypeTag, "channel_update"),
                                      (GossipMetrics.ReasonTag, "duplicate_update")));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.InvalidSignature)));
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.chain.lookups",
                                      (GossipMetrics.StatusTag, "found")));
    }

    private async Task<GraphTestKit> CreateKitWithChannelAsync(Action<GossipGraphOptions>? configure = null)
    {
        var kit = new GraphTestKit(configure: configure, metrics: _metrics);
        kit.FundingFound();
        var result = await ProcessAsync(kit, GraphTestKit.CreatePeer(0x70), Announcement());
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        return kit;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
    }

    private static ChannelAnnouncementMessage Announcement() =>
        GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding);

    /// <summary>An update of <paramref name="direction"/> signed by <paramref name="signer"/> (a forgery unless it
    /// is that direction's node), with a fee that makes its fields differ per <paramref name="fee"/>.</summary>
    private static ChannelUpdateMessage Update(TestGossipKey signer, byte direction, uint timestamp, int fee) =>
        GraphTestKit.SignedChannelUpdate(s_scid, signer, direction, timestamp, (uint)(1_000 + fee));

    private static Task<GossipIngressResult> ProcessAsync(GraphTestKit kit, Mock<IPeerService> peer,
                                                          Domain.Protocol.Interfaces.IMessage message) =>
        kit.Ingress.ProcessAsync(peer.Object, message, 0, TestContext.Current.CancellationToken);
}