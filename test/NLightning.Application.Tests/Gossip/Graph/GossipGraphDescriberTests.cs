using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Gossip;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Sync;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Queries;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Sync;

/// <summary>
/// BOLT 7 G5-T4: what <c>describegraph</c> reads, from a real store, ingress and sync manager (the IPC side is
/// <c>Daemon.Tests/Ipc/Handlers/DescribeGraphIpcHandlerTests</c>).
/// </summary>
public class GossipGraphDescriberTests
{
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Given_AGraphWithAnOrphanAndAPendingAnnouncement_When_Described_Then_CountsMemoryAndIngressAreReported()
    {
        // Arrange: the store's graph (2 channels, 3 policies, 2 announced nodes), an update for an unknown channel and
        // an announcement without update (NL-406: pending, not in the graph)
        var kit = await GraphStoreTests.CreateGraphAsync();
        var orphan = GraphTestKit.SignedChannelUpdate(new ShortChannelId(999, 1, 0), new TestGossipKey(1), 0, s_now);
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, orphan, 0,
                                                    TestContext.Current.CancellationToken);
        Assert.Equal(GossipIngressOutcome.Orphaned, result.Outcome);
        var pending = GraphTestKit.SignedChannelAnnouncement(new ShortChannelId(998, 1, 0), new TestGossipKey(41),
                                                             new TestGossipKey(42), new TestGossipKey(43),
                                                             new TestGossipKey(44));
        Assert.Equal(GossipIngressOutcome.Pending,
                     (await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, pending, 0,
                                                     TestContext.Current.CancellationToken)).Outcome);
        Assert.True(kit.Store.MarkSpent(new ShortChannelId(115, 1, 0), 300));
        var describer = new GossipGraphDescriber(kit.Store, kit.Ingress);

        // Act
        var description = describer.Describe();

        // Assert
        Assert.False(description.IsLoaded);
        Assert.Equal((2, 1, 0, 0, 0), (description.Channels, description.SpentChannels,
                                       description.UnverifiedChannels, description.OwnChannels,
                                       description.ChannelsWithoutPolicy));
        Assert.Equal((3, 0), (description.Policies, description.DisabledPolicies));
        Assert.Equal((2, 3), (description.AnnouncedNodes, description.GraphNodes));
        Assert.Equal(1_000_000UL, description.CapacitySat); // bc is spent: only ab counts
        Assert.Equal(kit.Store.PendingChanges, description.PendingWrites);
        Assert.Equal(kit.Store.GetMemoryEstimate(), description.Memory);
        Assert.Equal(new GossipIngressState(0, 0, 1, 1), description.Ingress);
        Assert.Null(description.Sync);
    }

    [Fact]
    public async Task Given_TaprootGossip_When_Described_Then_TheV2CountsAndPendingV2AnnouncementsAreReported()
    {
        // Arrange (NL-1141): a v2 channel through the real ingress with both v2 updates (bob's disabled) and alice's
        // node_announcement_2, and a keyless channel_announcement_2 waiting for its first update (NL-1140)
        const uint tip = 3_000;
        var alice = new GossipV2TestKey(1);
        var bob = new GossipV2TestKey(2);
        var aliceFunding = new GossipV2TestKey(11);
        var bobFunding = new GossipV2TestKey(12);
        var scid = new ShortChannelId(900, 1, 0);
        var kit = new GraphTestKit(gossipV2: true, tipHeight: tip);
        kit.OutputFound(GossipV2TestSigner.TaprootFundingScript(aliceFunding.PubKey, bobFunding.PubKey));
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        var (node1, node2) = ((ReadOnlySpan<byte>)alice.PubKey).SequenceCompareTo(bob.PubKey) < 0
                                 ? (alice, bob)
                                 : (bob, alice);
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await kit.Ingress.ProcessAsync(
                          peer, new ChannelAnnouncement2Message(GossipV2TestSigner.SignedChannelAnnouncement2(
                                                                    ChainConstants.Regtest, scid, 1_000_000, alice,
                                                                    bob, aliceFunding, bobFunding,
                                                                    GraphTestKit.TxIdFor(scid))), 0, ct)).Outcome);
        await kit.Ingress.ProcessAsync(peer, new ChannelUpdate2Message(
                                           GossipV2TestSigner.SignedChannelUpdate2(ChainConstants.Regtest, scid, 0,
                                                                                   tip - 1, node1)), 0, ct);
        await kit.Ingress.ProcessAsync(peer, new ChannelUpdate2Message(
                                           GossipV2TestSigner.SignedChannelUpdate2(ChainConstants.Regtest, scid, 1,
                                                                                   tip - 1, node2,
                                                                                   disableFlags: 1)), 0, ct);
        await kit.Ingress.ProcessAsync(peer, new NodeAnnouncement2Message(
                                           GossipV2TestSigner.SignedNodeAnnouncement2(alice, tip - 1)), 0, ct);
        var keylessScid = new ShortChannelId(901, 1, 0);
        var unsigned = ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, ReadOnlySpan<byte>.Empty,
                                                          keylessScid, 1_000_000, node1.PubKey, node2.PubKey, null,
                                                          null, ReadOnlySpan<byte>.Empty,
                                                          GraphTestKit.TxIdFor(keylessScid), 0);
        var keyless = unsigned.WithSignature(new Domain.Crypto.ValueObjects.CompactSignature(
                                                 GossipV2TestSigner.MusigSign([node1, node2],
                                                                              (byte[])unsigned.GetSignatureHash())));
        Assert.Equal(GossipIngressOutcome.Pending,
                     (await kit.Ingress.ProcessAsync(peer, new ChannelAnnouncement2Message(keyless), 0, ct)).Outcome);
        var describer = new GossipGraphDescriber(kit.Store, kit.Ingress);

        // Act
        var description = describer.Describe();

        // Assert
        Assert.Equal(new GraphV2Counts(1, 0, 2, 1, 1), description.V2);
        Assert.Equal((1, 0, 0), (description.Channels, description.Policies, description.ChannelsWithoutPolicy));
        Assert.NotNull(description.Ingress);
        Assert.Equal((0, 1), (description.Ingress.PendingAnnouncements, description.Ingress.PendingAnnouncements2));
    }

    [Fact]
    public async Task Given_ASyncPeerAndAPlainPeer_When_Described_Then_EachConnectionsSyncStateIsReported()
    {
        // Arrange: a gossip_queries peer that completes its range sync and sends its filter, and one without queries
        var graph = new SyncTestGraph();
        var ingress = new Mock<IGossipIngress>();
        ingress.SetupGet(i => i.IsEnabled).Returns(true);
        using var manager = new GossipSyncManager(graph.Store,
                                                  Microsoft.Extensions.Options.Options.Create(new GossipSyncOptions()),
                                                  Microsoft.Extensions.Options.Options.Create(new NodeOptions
                                                  {
                                                      BitcoinNetwork = BitcoinNetwork.Resolve("regtest")
                                                  }), NullLogger<GossipSyncManager>.Instance, graph.Kit.Clock,
                                                  ingress.Object, getTipHeight: () => 500);
        var syncPeer = new FakeGossipPeer(1);
        var plainPeer = new FakeGossipPeer(2, gossipQueries: false);
        manager.OnPeerInitialized(syncPeer);
        manager.OnPeerInitialized(plainPeer);
        await syncPeer.NextAsync<QueryChannelRangeMessage>();
        manager.HandleMessage(syncPeer, RangeReplyCollectorTests.Reply(0, 501, true));
        await syncPeer.NextAsync<GossipTimestampFilterMessage>();
        await plainPeer.NextAsync<GossipTimestampFilterMessage>();
        manager.HandleMessage(syncPeer, new GossipTimestampFilterMessage(
                                            new GossipTimestampFilterPayload(ChainConstants.Regtest, 1_000, 2_000)));
        await manager.WhenIdleAsync(syncPeer, TestContext.Current.CancellationToken);
        var describer = new GossipGraphDescriber(graph.Store, syncManager: manager);

        // Act
        var description = describer.Describe();

        // Assert
        Assert.Null(description.Ingress);
        Assert.NotNull(description.Sync);
        Assert.True(description.Sync.HasCompletedInitialSync);
        var peers = description.Sync.Peers.ToDictionary(p => p.PeerId);
        var sync = peers[syncPeer.PeerPubKey];
        Assert.True(sync is { IsInitialized: true, SupportsQueries: true, SupportsQueriesEx: false, IsSyncPeer: true });
        Assert.False(sync.IsRangeSyncRunning);
        Assert.Equal(graph.Kit.Clock.GetUtcNow(), sync.LastRangeSyncAt);
        Assert.Equal(new GossipTimestampFilter(1_000, 2_000), sync.PeerFilter);
        Assert.Equal(new GossipTimestampFilter(s_now - 1_209_600, uint.MaxValue), sync.OurFilter);
        Assert.False(sync.IsQuerySlotPoisoned);
        var plain = peers[plainPeer.PeerPubKey];
        Assert.False(plain.SupportsQueries || plain.IsSyncPeer);
        Assert.Null(plain.LastRangeSyncAt);
        Assert.Null(plain.PeerFilter);
        Assert.Equal(GossipTimestampFilter.None, plain.OurFilter);
    }
}