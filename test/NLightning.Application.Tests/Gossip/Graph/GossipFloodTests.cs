namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Metrics;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Metrics;

/// <summary>
/// BOLT 7 plan G5-T2 proof (fuzz-style): a peer flooding gossip with invalid signatures through the real queues and
/// workers is warned, disconnected and banned, its later gossip dropped at the door, while another peer's valid gossip,
/// sent at the same time, is all applied and that peer is never warned or disconnected.
/// </summary>
public class GossipFloodTests
{
    private const int FloodSize = 400;
    private const int HonestChannels = 30;

    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly ShortChannelId s_known = new(110, 1, 0);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Given_APeerFloodingInvalidSignatures_When_AnotherPeerSendsValidGossip_Then_TheFlooderIsBannedAndTheOtherUnaffected()
    {
        // Arrange: a stored channel the flood targets, two workers, a seeded fuzzer
        using var metrics = new GossipMetrics();
        using var recorder = new GossipMetricsRecorder(metrics);
        var kit = new GraphTestKit(configure: o => o.Workers = 2, metrics: metrics);
        kit.FundingFound();
        var bystander = GraphTestKit.CreatePeer(0x70);
        await kit.Ingress.ProcessAsync(bystander.Object,
                                       GraphTestKit.SignedChannelAnnouncement(s_known, s_alice, s_bob,
                                                                              new TestGossipKey(11),
                                                                              new TestGossipKey(12)), 0,
                                       TestContext.Current.CancellationToken);

        var flooder = GraphTestKit.CreatePeer(0x66);
        var flooderDisconnects = 0;
        flooder.Setup(p => p.Disconnect(It.IsAny<Exception>()))
               .Callback(() => Interlocked.Increment(ref flooderDisconnects));
        var honest = GraphTestKit.CreatePeer(0x67);
        var random = new Random(20260926);
        var flood = Enumerable.Range(0, FloodSize).Select(i => FuzzMessage(random, i)).ToList();
        var valid = Enumerable.Range(0, HonestChannels).SelectMany(ValidChannelGossip).ToList();

        // Act: both peers hand their gossip over concurrently, as two read loops would
        var flooding = Task.Run(() => flood.Count(m => kit.Ingress.TryEnqueue(flooder.Object, m)),
                                TestContext.Current.CancellationToken);
        var sending = Task.Run(() => valid.Count(m => kit.Ingress.TryEnqueue(honest.Object, m)),
                               TestContext.Current.CancellationToken);
        await Task.WhenAll(flooding, sending);
        await WaitUntilAsync(() => AllValidApplied(kit) && kit.Ingress.QueuedCount == 0);
        // A late message of the flooder (a new connection after the ban) is refused at the door
        var reconnected = GraphTestKit.CreatePeer(0x66);
        var afterBan = kit.Ingress.TryEnqueue(reconnected.Object, FuzzMessage(random, FloodSize));
        await kit.Ingress.StopAsync();

        // Assert: the flooder is banned (persisted) and was told so, its flood mostly never validated
        Assert.True(kit.Store.IsBanned(flooder.Object.PeerPubKey));
        flooder.Verify(p => p.Disconnect(It.Is<WarningException>(e => e.Message.Contains("Too much invalid gossip"))),
                       Times.Once);
        Assert.False(afterBan);
        Assert.Contains(kit.Repository.Bans.Keys, k => k == flooder.Object.PeerPubKey);
        Assert.Equal(1, recorder.Sum("nlightning.gossip.peers.banned"));
        var bannedDrops = recorder.Sum("nlightning.gossip.messages.rejected",
                                       (GossipMetrics.ReasonTag, GossipMetricReasons.BannedPeer));
        Assert.True(bannedDrops >= FloodSize / 2, $"only {bannedDrops} flood messages dropped after the ban");

        // The honest peer's gossip is all in the graph, and it was never warned or disconnected
        Assert.True(AllValidApplied(kit));
        Assert.Equal(valid.Count, await sending);
        Assert.False(kit.Store.IsBanned(honest.Object.PeerPubKey));
        honest.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        honest.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);

        // The flood never changed the targeted channel or node
        Assert.True(kit.Store.TryGetChannel(s_known, out var known));
        Assert.Null(known.Policy1);
        Assert.Null(known.Policy2);
        Assert.False(kit.Store.TryGetNode(s_alice.PubKey, out _));
    }

    /// <summary>
    /// One fuzzed message: an update of the known channel, an announcement of one of its nodes or a new channel's
    /// announcement, each with random signatures (sometimes not even a valid encoding) and random fields.
    /// </summary>
    private static IMessage FuzzMessage(Random random, int index)
    {
        var signature = new CompactSignature(RandomBytes(random, 64));
        switch (index % 3)
        {
            case 0:
                {
                    var update = new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest,
                                                          s_known, s_now - (uint)random.Next(1, 100_000),
                                                          ChannelUpdatePayload.MessageFlagMustBeOne,
                                                          (byte)random.Next(0, 2), (ushort)random.Next(1, 500),
                                                          (ulong)random.Next(0, 10_000), (uint)random.Next(),
                                                          (uint)random.Next(0, 5_000), 500_000_000);
                    return new ChannelUpdateMessage(update.WithSignature(signature));
                }
            case 1:
                {
                    var node = random.Next(0, 2) == 0 ? s_alice : s_bob;
                    var announcement = new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature,
                                                                   ReadOnlyMemory<byte>.Empty,
                                                                   s_now - (uint)random.Next(1, 100_000),
                                                                   node.PubKey, RandomBytes(random, 3),
                                                                   NodeAnnouncementPayload.EncodeAlias(
                                                                       $"fuzz{index}"),
                                                                   new byte[] { 1, 127, 0, 0, 1, 0x26, 0x07 });
                    return new NodeAnnouncementMessage(announcement.WithSignature(signature));
                }
            default:
                {
                    var scid = new ShortChannelId((uint)random.Next(1_000, 100_000), (uint)random.Next(1, 1_000), 0);
                    var announcement = GraphTestKit.SignedChannelAnnouncement(scid, s_alice, s_bob,
                                                                              new TestGossipKey(11),
                                                                              new TestGossipKey(12)).Payload;
                    return new ChannelAnnouncementMessage(
                        announcement.WithSignatures(signature, new CompactSignature(RandomBytes(random, 64)),
                                                    announcement.BitcoinSignature1, announcement.BitcoinSignature2));
                }
        }
    }

    /// <summary>A channel between two fresh nodes: its announcement and both updates, validly signed.</summary>
    private static IEnumerable<IMessage> ValidChannelGossip(int index)
    {
        var (nodeA, nodeB) = Nodes(index);
        var scid = ScidOf(index);
        yield return GraphTestKit.SignedChannelAnnouncement(scid, nodeA, nodeB, new TestGossipKey((byte)(index + 130)),
                                                            new TestGossipKey((byte)(index + 180)));
        yield return GraphTestKit.SignedChannelUpdate(scid, nodeA, GraphTestKit.DirectionOf(nodeA, nodeB), s_now - 10);
        yield return GraphTestKit.SignedChannelUpdate(scid, nodeB, GraphTestKit.DirectionOf(nodeB, nodeA), s_now - 10);
    }

    private static bool AllValidApplied(GraphTestKit kit)
    {
        for (var i = 0; i < HonestChannels; i++)
        {
            if (!kit.Store.TryGetChannel(ScidOf(i), out var channel) || channel.Policy1 is null
             || channel.Policy2 is null)
                return false;
        }

        return true;
    }

    private static (TestGossipKey, TestGossipKey) Nodes(int index) =>
        (new TestGossipKey((byte)(index * 2 + 20)), new TestGossipKey((byte)(index * 2 + 21)));

    private static ShortChannelId ScidOf(int index) => new(200 + (uint)index, 1, 0);

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), "the ingress did not finish in time");
    }
}