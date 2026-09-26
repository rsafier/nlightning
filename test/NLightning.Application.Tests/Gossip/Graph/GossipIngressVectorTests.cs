using NLightning.Tests.Utils.Vectors;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Gossip.Enums;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// Plan BOLT7 G2-T4 proof over the messages captured from LND 0.20 and CLN v26.06.8 (<see cref="Bolt7Vectors"/>): the
/// whole ingress pipeline (validator, real signature verifier, a funding lookup that finds every output) accepts every
/// announcement and update, in any order, and rejects a tampered copy.
/// </summary>
public class GossipIngressVectorTests
{
    private static readonly List<IMessage> s_lnd = Parse(Bolt7Vectors.Lnd);
    private static readonly List<IMessage> s_cln = Parse(Bolt7Vectors.Cln);

    [Fact]
    public async Task Given_LndGraphDump_When_Processed_Then_EveryChannelUpdateAndNewestNodeAnnouncementIsAccepted()
    {
        // Arrange: the capture holds 3 announced channels (203x1x0, 215x1x0, 227x1x0) with 6 updates, the update of
        // an unannounced channel (249x1x0, alice's channel to us), and two node_announcements of one node (the newer
        // first) plus one of another
        var kit = CreateKit(s_lnd);
        var peer = GraphTestKit.CreatePeer();

        // Act
        var results = new List<(IMessage Message, GossipIngressResult Result)>();
        foreach (var message in s_lnd.Where(m => m is not AnnouncementSignaturesMessage))
            results.Add((message,
                         await kit.Ingress.ProcessAsync(peer.Object, message, 0,
                                                        TestContext.Current.CancellationToken)));

        // Assert
        Assert.All(results.Where(r => r.Message is ChannelAnnouncementMessage),
                   r => Assert.Equal(GossipIngressOutcome.Accepted, r.Result.Outcome));
        var updates = results.Where(r => r.Message is ChannelUpdateMessage).ToList();
        Assert.Equal(6, updates.Count(r => r.Result.Outcome == GossipIngressOutcome.Accepted));
        Assert.Equal(GossipIngressOutcome.Orphaned,
                     Assert.Single(updates, r => r.Result.Outcome != GossipIngressOutcome.Accepted).Result.Outcome);
        var nodes = results.Where(r => r.Message is NodeAnnouncementMessage).ToList();
        Assert.Equal(2, nodes.Count(r => r.Result.Outcome == GossipIngressOutcome.Accepted));
        Assert.Equal(Domain.Gossip.Validation.GossipRejectReason.NotNewer,
                     Assert.Single(nodes, r => r.Result.Outcome != GossipIngressOutcome.Accepted).Result
                                                                                                   .RejectReason);

        Assert.Equal(3, kit.Store.ChannelCount);
        Assert.Equal(2, kit.Store.NodeCount);
        var snapshot = kit.Store.GetSnapshot();
        Assert.Equal(6, snapshot.Channels.Sum(c => (c.Policy1 is null ? 0 : 1) + (c.Policy2 is null ? 0 : 1)));
        Assert.All(snapshot.Channels, c => Assert.Equal(1_000_000UL, c.CapacitySat));
        Assert.Equal(["alice", "bob"], snapshot.Nodes.Select(n => n.AliasText).Order());
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        peer.Verify(p => p.SendWarningAsync(It.IsAny<Domain.Exceptions.WarningException>()), Times.Never);
    }

    [Fact]
    public async Task Given_LndGraphDump_When_UpdatesAndNodesArriveBeforeTheirChannels_Then_TheyAreReplayedWhenTheChannelsArrive()
    {
        // Arrange
        var kit = CreateKit(s_lnd);
        var peer = GraphTestKit.CreatePeer();
        var early = s_lnd.Where(m => m is ChannelUpdateMessage or NodeAnnouncementMessage).ToList();
        var announcements = s_lnd.OfType<ChannelAnnouncementMessage>().ToList();

        // Act
        var earlyResults = new List<GossipIngressResult>();
        foreach (var message in early)
            earlyResults.Add(await kit.Ingress.ProcessAsync(peer.Object, message, 0,
                                                            TestContext.Current.CancellationToken));
        foreach (var message in announcements)
            Assert.Equal(GossipIngressOutcome.Accepted,
                         (await kit.Ingress.ProcessAsync(peer.Object, message, 0,
                                                         TestContext.Current.CancellationToken)).Outcome);

        // Assert: everything waited, then everything was applied; only the unannounced channel's update still waits
        Assert.All(earlyResults, r => Assert.Equal(GossipIngressOutcome.Orphaned, r.Outcome));
        var snapshot = kit.Store.GetSnapshot();
        Assert.Equal(6, snapshot.Channels.Sum(c => (c.Policy1 is null ? 0 : 1) + (c.Policy2 is null ? 0 : 1)));
        Assert.Equal(2, kit.Store.NodeCount);
        Assert.Equal(1, kit.Ingress.Orphans.Count);
        var newest = s_lnd.OfType<NodeAnnouncementMessage>().GroupBy(n => n.Payload.NodeId)
                          .Select(g => g.MaxBy(n => n.Payload.Timestamp)!).ToList();
        Assert.All(newest, n =>
        {
            Assert.True(kit.Store.TryGetNode(n.Payload.NodeId, out var stored));
            Assert.Equal(n.Payload.Timestamp, stored.Timestamp);
        });
    }

    [Fact]
    public async Task Given_ClnGossip_When_Processed_Then_TheAnnouncedChannelItsUpdateAndNodeAreAccepted()
    {
        // Arrange
        var kit = CreateKit(s_cln);
        var peer = GraphTestKit.CreatePeer();
        var announced = s_cln.OfType<ChannelAnnouncementMessage>().Single().Payload.ShortChannelId;

        // Act
        var results = new List<(IMessage Message, GossipIngressResult Result)>();
        foreach (var message in s_cln.Where(m => m is not AnnouncementSignaturesMessage))
            results.Add((message,
                         await kit.Ingress.ProcessAsync(peer.Object, message, 0,
                                                        TestContext.Current.CancellationToken)));

        // Assert: the announced channel's update is applied; the update of CLN's private channel to us is not graph
        // data (dont_forward, or no announcement) and waits or is ignored, never warned
        Assert.Equal(GossipIngressOutcome.Accepted,
                     results.Single(r => r.Message is ChannelAnnouncementMessage).Result.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     results.Single(r => r.Message is NodeAnnouncementMessage).Result.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     results.Single(r => r.Message is ChannelUpdateMessage { Payload: var u }
                                      && u.ShortChannelId == announced).Result.Outcome);
        Assert.DoesNotContain(results, r => r.Result.Outcome == GossipIngressOutcome.Warned);
        Assert.True(kit.Store.TryGetChannel(announced, out var channel));
        Assert.NotNull(channel.Policy1 ?? channel.Policy2);
    }

    [Fact]
    public async Task Given_CapturedAnnouncementWithTamperedSignature_When_Processed_Then_WarnedAndDisconnected()
    {
        // Arrange
        var kit = CreateKit(s_lnd);
        var peer = GraphTestKit.CreatePeer();
        var payload = s_lnd.OfType<ChannelAnnouncementMessage>().First().Payload.GetBytes();
        payload[10] ^= 0x01;
        var tampered = new ChannelAnnouncementMessage(ChannelAnnouncementPayload.Parse(payload));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, tampered, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        Assert.True(result.CloseConnection);
        peer.Verify(p => p.Disconnect(It.IsAny<Domain.Exceptions.WarningException>()), Times.Once);
        Assert.Equal(0, kit.Store.ChannelCount);
        kit.FundingLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_CapturedUpdateWithTamperedField_When_ItsChannelIsKnown_Then_WarnedAndDisconnected()
    {
        // Arrange
        var kit = CreateKit(s_lnd);
        var peer = GraphTestKit.CreatePeer();
        foreach (var announcement in s_lnd.OfType<ChannelAnnouncementMessage>())
            await kit.Ingress.ProcessAsync(peer.Object, announcement, 0, TestContext.Current.CancellationToken);
        var payload = s_lnd.OfType<ChannelUpdateMessage>().First().Payload.GetBytes();
        payload[^1] ^= 0x01; // htlc_maximum_msat, covered by the signature
        var tampered = new ChannelUpdateMessage(ChannelUpdatePayload.Parse(payload));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, tampered, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        peer.Verify(p => p.Disconnect(It.IsAny<Domain.Exceptions.WarningException>()), Times.Once);
        Assert.All(kit.Store.GetSnapshot().Channels, c => Assert.True(c.Policy1 is null && c.Policy2 is null));
    }

    [Fact]
    public async Task Given_CapturedAnnouncement_When_ProcessedTwice_Then_TheCopyIsIgnoredWithoutAChainLookup()
    {
        // Arrange
        var kit = CreateKit(s_lnd);
        var peer = GraphTestKit.CreatePeer();
        var announcement = s_lnd.OfType<ChannelAnnouncementMessage>().First();
        await kit.Ingress.ProcessAsync(peer.Object, announcement, 0, TestContext.Current.CancellationToken);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, announcement, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        kit.FundingLookup.Verify(l => l.VerifyAsync(It.IsAny<Domain.Channels.ValueObjects.ShortChannelId>(),
                                                    It.IsAny<Domain.Crypto.ValueObjects.CompactPubKey>(),
                                                    It.IsAny<Domain.Crypto.ValueObjects.CompactPubKey>(),
                                                    It.IsAny<Domain.Money.LightningMoney?>(),
                                                    It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(FundingOutputStatus.OutputSpentOrMissing, GossipIngressOutcome.Ignored)]
    [InlineData(FundingOutputStatus.ScriptMismatch, GossipIngressOutcome.Ignored)]
    [InlineData(FundingOutputStatus.TransactionIndexOutOfRange, GossipIngressOutcome.Ignored)]
    [InlineData(FundingOutputStatus.BlockUnavailable, GossipIngressOutcome.Ignored)]
    [InlineData(FundingOutputStatus.ChainUnavailable, GossipIngressOutcome.Deferred)]
    [InlineData(FundingOutputStatus.BlockNotFound, GossipIngressOutcome.Deferred)]
    [InlineData(FundingOutputStatus.ChainMoved, GossipIngressOutcome.Deferred)]
    [InlineData(FundingOutputStatus.OutputSpentInMempool, GossipIngressOutcome.Deferred)]
    public async Task Given_CapturedAnnouncement_When_TheFundingCheckFails_Then_IgnoredOrDeferred(
        FundingOutputStatus status, GossipIngressOutcome expected)
    {
        // Arrange
        var kit = new GraphTestKit(now: ClockFor(s_lnd));
        kit.FundingFails(status);
        var peer = GraphTestKit.CreatePeer();

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, s_lnd.OfType<ChannelAnnouncementMessage>().First(),
                                                    0, TestContext.Current.CancellationToken);

        // Assert: never a warning (a chain answer is no proof against the peer)
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(0, kit.Store.ChannelCount);
        peer.Verify(p => p.SendWarningAsync(It.IsAny<Domain.Exceptions.WarningException>()), Times.Never);
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_CapturedAnnouncement_When_TheFundingHasFiveConfirmations_Then_DeferredAndAcceptedOnceDeepEnough()
    {
        // Arrange
        var kit = new GraphTestKit(now: ClockFor(s_lnd));
        kit.FundingFound(confirmations: 5);
        var peer = GraphTestKit.CreatePeer();
        var announcement = s_lnd.OfType<ChannelAnnouncementMessage>().First();

        // Act
        var early = await kit.Ingress.ProcessAsync(peer.Object, announcement, 0,
                                                   TestContext.Current.CancellationToken);
        kit.FundingFound(confirmations: 6);
        var retried = await kit.Ingress.ProcessAsync(peer.Object, announcement, 1,
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Deferred, early.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, retried.Outcome);
    }

    [Fact]
    public async Task Given_PrunedBlockAndSkipUnavailable_When_Processed_Then_TheChannelIsKeptUnverifiedWithoutCapacity()
    {
        // Arrange
        var kit = new GraphTestKit(now: ClockFor(s_lnd),
                                   configure: o => o.FundingValidation = FundingValidationMode.SkipUnavailable);
        kit.FundingFails(FundingOutputStatus.BlockUnavailable);
        var announcement = s_lnd.OfType<ChannelAnnouncementMessage>().First();

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, announcement, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(announcement.Payload.ShortChannelId, out var channel));
        Assert.Equal(Domain.Gossip.Graph.GraphChannelVerification.Unverified, channel.Verification);
        Assert.Null(channel.CapacitySat);
    }

    internal static GraphTestKit CreateKit(IReadOnlyList<IMessage> messages)
    {
        var kit = new GraphTestKit(now: ClockFor(messages));
        kit.FundingFound();
        return kit;
    }

    /// <summary>A clock just after the newest captured update, so none of them counts as stale or future.</summary>
    internal static DateTimeOffset ClockFor(IEnumerable<IMessage> messages) =>
        DateTimeOffset.FromUnixTimeSeconds(messages.OfType<ChannelUpdateMessage>().Max(m => m.Payload.Timestamp)
                                         + 60);

    internal static List<IMessage> Parse(IEnumerable<Bolt7CapturedMessage> vectors) =>
        vectors.Select(v => v.Type switch
        {
            256 => (IMessage)new ChannelAnnouncementMessage(ChannelAnnouncementPayload.Parse(v.Payload)),
            257 => new NodeAnnouncementMessage(NodeAnnouncementPayload.Parse(v.Payload)),
            258 => new ChannelUpdateMessage(ChannelUpdatePayload.Parse(v.Payload)),
            259 => new AnnouncementSignaturesMessage(AnnouncementSignaturesPayload.Parse(v.Payload)),
            _ => throw new InvalidOperationException($"Unexpected type {v.Type}")
        }).ToList();
}