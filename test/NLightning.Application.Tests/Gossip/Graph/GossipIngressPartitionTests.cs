namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Metrics;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Metrics;

/// <summary>
/// NL-408: the ingress keeps one queue per worker, chosen by short channel id, so a channel's announcement is handled
/// before its updates (in arrival order) and the initial sync no longer orphans them; channels still spread over every
/// worker, and the caps hold across the queues.
/// </summary>
public class GossipIngressPartitionTests
{
    private const int Channels = 150;

    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();
    private static readonly TestGossipKey[] s_nodes = Enumerable.Range(0, 12).Select(i => new TestGossipKey((byte)(90 + i)))
                                                                .ToArray();

    [Fact]
    public void Given_AChannelsMessages_When_Partitioned_Then_TheyShareAQueueAndChannelsSpreadOverAll()
    {
        // Arrange: a block's worth of neighbouring short channel ids (the ids of one block differ in a few bits)
        const int partitions = 4;
        var counts = new int[partitions];

        // Act
        for (uint block = 800_000; block < 800_010; block++)
        {
            for (uint index = 0; index < 100; index++)
            {
                var scid = new ShortChannelId(block, index, 0);
                var announcement = GraphTestKit.SignedChannelAnnouncement(scid, s_nodes[0], s_nodes[1], s_nodes[2],
                                                                          s_nodes[3]);
                var partition = GossipIngress.PartitionOf(announcement, partitions);
                Assert.Equal(partition,
                             GossipIngress.PartitionOf(GraphTestKit.SignedChannelUpdate(scid, s_nodes[0], 0, s_now),
                                                       partitions));
                Assert.Equal(partition,
                             GossipIngress.PartitionOf(GraphTestKit.SignedChannelUpdate(scid, s_nodes[1], 1, s_now),
                                                       partitions));
                counts[partition]++;
            }
        }

        // Assert: 1,000 channels, each queue within a quarter of its fair share
        Assert.All(counts, c => Assert.InRange(c, 190, 310));
        Assert.Equal(0, GossipIngress.PartitionOf(GraphTestKit.SignedNodeAnnouncement(s_nodes[0], s_now), 1));
        Assert.InRange(GossipIngress.PartitionOf(GraphTestKit.SignedNodeAnnouncement(s_nodes[0], s_now), partitions), 0,
                       partitions - 1);
    }

    [Fact]
    public async Task Given_ASyntheticInitialSyncFromTwoPeers_When_FourWorkersProcessIt_Then_NoUpdateIsOrphaned()
    {
        // Arrange: four workers, a chain lookup that takes a little while (as bitcoind does), and two peers that
        // each stream the same graph, every channel's announcement followed by its two updates
        using var metrics = new GossipMetrics();
        using var recorder = new GossipMetricsRecorder(metrics);
        var kit = new GraphTestKit(configure: o => o.Workers = 4, metrics: metrics);
        var random = new Random(408);
        kit.FundingLookup.Setup(l => l.VerifyAsync(It.IsAny<ShortChannelId>(), It.IsAny<CompactPubKey>(),
                                                   It.IsAny<CompactPubKey>(), It.IsAny<LightningMoney?>(),
                                                   It.IsAny<CancellationToken>()))
           .Returns(async (ShortChannelId scid, CompactPubKey _, CompactPubKey _, LightningMoney? _,
                           CancellationToken ct) =>
            {
                int delay;
                lock (random)
                    delay = random.Next(0, 4);
                await Task.Delay(delay, ct);
                return FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, GraphTestKit.TxIdFor(scid),
                                                            LightningMoney.Satoshis(1_000_000), [0x00, 0x20], 6);
            });
        var gossip = Enumerable.Range(0, Channels).SelectMany(ChannelGossip).ToList();
        var first = GraphTestKit.CreatePeer(0x71).Object;
        var second = GraphTestKit.CreatePeer(0x72).Object;
        await kit.Ingress.StartAsync();
        Assert.Equal(4, kit.Ingress.PartitionCount);

        // Act: both peers hand their streams over concurrently, as two read loops would
        var sending = new[]
        {
            Task.Run(() => gossip.Count(m => kit.Ingress.TryEnqueue(first, m)), TestContext.Current.CancellationToken),
            Task.Run(() => gossip.Count(m => kit.Ingress.TryEnqueue(second, m)), TestContext.Current.CancellationToken)
        };
        var queued = await Task.WhenAll(sending);
        await WaitUntilAsync(() => AllApplied(kit) && kit.Ingress.QueuedCount == 0);
        await kit.Ingress.StopAsync();

        // Assert
        Assert.All(queued, q => Assert.Equal(gossip.Count, q));
        Assert.True(AllApplied(kit));
        Assert.Equal(0, recorder.Sum("nlightning.gossip.messages.orphaned"));
        Assert.Equal(0, kit.Ingress.Orphans.Count);
        Assert.Equal(0, kit.Ingress.PendingAnnouncementCount);
        Assert.Equal(Channels, recorder.Sum("nlightning.gossip.messages.accepted",
                                            (GossipMetrics.TypeTag, "channel_announcement")));
        Assert.Equal(2 * Channels, recorder.Sum("nlightning.gossip.messages.accepted",
                                                (GossipMetrics.TypeTag, "channel_update")));
        Assert.Equal(0, kit.Ingress.DroppedCount);
    }

    [Fact]
    public void Given_MessagesOnSeveralQueues_When_TheGlobalCapIsReached_Then_TheCapHoldsAcrossTheQueues()
    {
        // Arrange: four queues, 5 messages in total; the workers never start (the graph load never completes)
        var store = new Mock<Application.Gossip.Graph.Interfaces.IGraphStore>();
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource().Task);
        var ingress = new GossipIngress(store.Object, new Infrastructure.Bitcoin.Gossip.GossipSignatureVerifier(),
                                        Mock.Of<Domain.Gossip.Interfaces.IFundingOutputLookup>(),
                                        Microsoft.Extensions.Options.Options.Create(new GossipGraphOptions
                                        {
                                            MaxQueued = 5,
                                            Workers = 4
                                        }),
                                        Microsoft.Extensions.Options.Options.Create(new Domain.Node.Options.NodeOptions
                                        {
                                            BitcoinNetwork = Domain.Protocol.ValueObjects.BitcoinNetwork.Regtest
                                        }),
                                        Microsoft.Extensions.Logging.Abstractions.NullLogger<GossipIngress>.Instance,
                                        new SettableTimeProvider(GraphTestKit.DefaultNow));
        var peer = GraphTestKit.CreatePeer().Object;

        // Act
        var results = Enumerable.Range(0, 8)
                                .Select(i => ingress.TryEnqueue(peer, GraphTestKit.SignedChannelUpdate(
                                                                    new ShortChannelId(700 + (uint)i, 1, 0),
                                                                    s_nodes[0], 0, s_now)))
                                .ToList();

        // Assert
        Assert.Equal(5, results.Count(r => r));
        Assert.Equal(5, ingress.QueuedCount);
        Assert.Equal(5, ingress.QueuedCountOf(peer.PeerPubKey));
        ingress.Dispose();
    }

    private static IEnumerable<IMessage> ChannelGossip(int index)
    {
        var nodeA = s_nodes[index % 6];
        var nodeB = s_nodes[6 + index % 6];
        var scid = new ShortChannelId(500 + (uint)(index / 7), (uint)(index % 7), 0);
        yield return GraphTestKit.SignedChannelAnnouncement(scid, nodeA, nodeB, new TestGossipKey((byte)(index + 1)),
                                                            new TestGossipKey((byte)(index + 101)));
        yield return GraphTestKit.SignedChannelUpdate(scid, nodeA, GraphTestKit.DirectionOf(nodeA, nodeB), s_now - 10);
        yield return GraphTestKit.SignedChannelUpdate(scid, nodeB, GraphTestKit.DirectionOf(nodeB, nodeA), s_now - 10);
    }

    private static bool AllApplied(GraphTestKit kit)
    {
        for (var index = 0; index < Channels; index++)
        {
            var scid = new ShortChannelId(500 + (uint)(index / 7), (uint)(index % 7), 0);
            if (!kit.Store.TryGetChannel(scid, out var channel) || channel.Policy1 is null || channel.Policy2 is null)
                return false;
        }

        return true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), "the ingress did not finish in time");
    }
}