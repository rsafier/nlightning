using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task Given_AGraphWithAnOrphan_When_Described_Then_CountsMemoryAndIngressAreReported()
    {
        // Arrange: the store's graph (2 channels, 3 policies, 2 announced nodes) and an update for an unknown channel
        var kit = await GraphStoreTests.CreateGraphAsync();
        var orphan = GraphTestKit.SignedChannelUpdate(new ShortChannelId(999, 1, 0), new TestGossipKey(1), 0, s_now);
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, orphan, 0,
                                                    TestContext.Current.CancellationToken);
        Assert.Equal(GossipIngressOutcome.Orphaned, result.Outcome);
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
        Assert.Equal(new GossipIngressState(0, 0, 1), description.Ingress);
        Assert.Null(description.Sync);
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