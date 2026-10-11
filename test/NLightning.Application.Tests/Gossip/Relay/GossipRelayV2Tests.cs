using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Gossip;

namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Models;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Graph;
using Sync;

/// <summary>
/// The relay of taproot gossip (BOLTs PR #1059, NL-878): others' v2 messages go only to connections that negotiated
/// <c>option_gossip_v2</c> and sent a <c>block_height_range</c>, inside it, 267 before 271 before 269, never back to
/// their origin; our own v2 messages go to every v2 connection; v1 connections get none of them.
/// </summary>
public class GossipRelayV2Tests : IDisposable
{
    private static readonly TimeSpan s_flushInterval = TimeSpan.FromSeconds(60);
    private static readonly ChainHash s_chain = ChainConstants.Regtest;
    private static readonly ShortChannelId s_scid = new(600, 1, 0);
    private static readonly GossipV2TestKey s_carol = new(61);
    private static readonly GossipV2TestKey s_dave = new(62);

    private readonly SyncTestGraph _graph = new();
    private readonly RelayTestClock _clock = new();
    private readonly List<GossipPeer> _peers = [];
    private readonly Dictionary<IPeerService, GossipTimestampFilter> _filters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IPeerService, GossipBlockHeightRange> _ranges = new(ReferenceEqualityComparer.Instance);
    private readonly Mock<IGossipSyncManager> _syncManager = new();
    private readonly GossipOriginTracker _origins = new();
    private GossipRelayScheduler _relay;

    public GossipRelayV2Tests()
    {
        _relay = CreateRelay(null);
    }

    private GossipRelayScheduler CreateRelay(GossipV2TestKey? ourNode,
                                              Action<GossipRelayOptions>? configure = null,
                                              IGossipPeerSender? sender = null)
    {
        var options = new GossipRelayOptions
        {
            RelayFlushInterval = s_flushInterval,
            RelayCollectInterval = TimeSpan.FromSeconds(10),
            RelayTickInterval = TimeSpan.FromSeconds(1)
        };
        configure?.Invoke(options);
        _syncManager.Setup(m => m.TryGetPeerFilter(It.IsAny<IPeerService>(), out It.Ref<GossipTimestampFilter>.IsAny))
                    .Returns(new TryGetFilter((IPeerService peer, out GossipTimestampFilter filter) =>
                                                  _filters.TryGetValue(peer, out filter)));
        _syncManager.Setup(m => m.TryGetPeerBlockHeightRange(It.IsAny<IPeerService>(),
                                                             out It.Ref<GossipBlockHeightRange>.IsAny))
                    .Returns(new TryGetRange((IPeerService peer, out GossipBlockHeightRange range) =>
                                                 _ranges.TryGetValue(peer, out range)));
        var directory = new Mock<IGossipPeerDirectory>();
        directory.Setup(d => d.GetConnectedPeers()).Returns(() => _peers.ToList());
        ISecureKeyManager? keys = null;
        if (ourNode is { } node)
        {
            var keyManager = new Mock<ISecureKeyManager>();
            keyManager.Setup(k => k.GetNodePubKey()).Returns(node.PubKey);
            keys = keyManager.Object;
        }

        return new GossipRelayScheduler(directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                        Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                        Options.Create(new GossipOptions()), _clock, sender, _graph.Store,
                                        _syncManager.Object, _origins, Options.Create(options), keys);
    }

    private delegate bool TryGetFilter(IPeerService peer, out GossipTimestampFilter filter);

    private delegate bool TryGetRange(IPeerService peer, out GossipBlockHeightRange range);

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task Given_NewV2Gossip_When_Flushed_Then_OnlyAGossipV2PeerWithARangeGetsItInOrder()
    {
        // Arrange
        var v2Peer = AddPeer(0x41, gossipV2: true, new GossipBlockHeightRange(0, uint.MaxValue));
        var v2PeerWithoutRange = AddPeer(0x42, gossipV2: true, null);
        var v1Peer = AddPeer(0x43, gossipV2: false, null);
        await BaselineAsync();
        var (announcement, update1, update2, node) = AddV2Channel();

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Equal([
            MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2, MessageTypes.ChannelUpdate2,
            MessageTypes.NodeAnnouncement2
        ], v2Peer.Sent.Select(m => m.Type));
        Assert.Equal(announcement.GetBytes(),
                     Assert.IsType<ChannelAnnouncement2Message>(v2Peer.Sent[0]).Payload.GetBytes());
        Assert.Equal(update1.GetBytes(), Assert.IsType<ChannelUpdate2Message>(v2Peer.Sent[1]).Payload.GetBytes());
        Assert.Equal(update2.GetBytes(), Assert.IsType<ChannelUpdate2Message>(v2Peer.Sent[2]).Payload.GetBytes());
        Assert.Equal(node.GetBytes(), Assert.IsType<NodeAnnouncement2Message>(v2Peer.Sent[3]).Payload.GetBytes());
        Assert.Empty(v2PeerWithoutRange.Sent);
        Assert.Empty(v1Peer.Sent);
    }

    [Fact]
    public async Task Given_ABlockHeightRange_When_Flushed_Then_OnlyTheUpdatesInsideGoOutWithTheirAnnouncement()
    {
        // Arrange: the updates are dated 2,500 and 2,501, the node announcement 2,500; the range is [2,501, 2,601)
        var peer = AddPeer(0x41, gossipV2: true, new GossipBlockHeightRange(2_501, 100));
        await BaselineAsync();
        var (_, _, update2, _) = AddV2Channel();

        // Act
        await FlushAllAsync();

        // Assert: the channel's announcement (dated by block 600, outside) is brought by its update inside
        Assert.Equal([MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2], peer.Sent.Select(m => m.Type));
        Assert.Equal(update2.GetBytes(), Assert.IsType<ChannelUpdate2Message>(peer.Sent[1]).Payload.GetBytes());
    }

    [Fact]
    public async Task Given_AV2UpdateAPeerSentUs_When_Flushed_Then_ItIsNotSentBackToThatPeer()
    {
        // Arrange
        var origin = AddPeer(0x41, gossipV2: true, new GossipBlockHeightRange(0, uint.MaxValue));
        var other = AddPeer(0x42, gossipV2: true, new GossipBlockHeightRange(0, uint.MaxValue));
        await BaselineAsync();
        var (_, update1, _, _) = AddV2Channel();
        _origins.Record(new ChannelUpdate2Message(update1), origin.PeerPubKey);

        // Act
        await FlushAllAsync();

        // Assert
        Assert.Equal(2, other.Sent.Count(m => m is ChannelUpdate2Message));
        var back = Assert.Single(origin.Sent.OfType<ChannelUpdate2Message>());
        Assert.NotEqual(update1.GetBytes(), back.Payload.GetBytes());
    }

    [Fact]
    public async Task Given_ANewFilterWithARange_When_Flushed_Then_TheV2BacklogInsideItIsSent()
    {
        // Arrange: the channel is in the graph before the peer asks (the baseline records it as seen)
        AddV2Channel();
        var peer = AddPeer(0x41, gossipV2: true, new GossipBlockHeightRange(2_400, 200));
        await BaselineAsync();

        // Act
        _syncManager.Raise(m => m.FilterReceived += null,
                           new GossipFilterReceivedEventArgs(peer, _filters[peer]));
        await FlushAllAsync();

        // Assert
        Assert.Equal([
            MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2, MessageTypes.ChannelUpdate2,
            MessageTypes.NodeAnnouncement2
        ], peer.Sent.Where(m => m.Type is MessageTypes.ChannelAnnouncement2 or MessageTypes.ChannelUpdate2
                                                                           or MessageTypes.NodeAnnouncement2)
                    .Select(m => m.Type));
    }

    [Fact]
    public async Task Given_AV2Backlog_When_PacedAtOneMessagePerTick_Then_ItPreservesOrderAndIsNotTruncatedByPendingCaps()
    {
        _relay.Dispose();
        _relay = CreateRelay(null, o =>
        {
            o.BacklogMessagesPerSecond = 1;
            o.MaxRelayPendingPerPeer = 1;
        });
        AddV2Channel();
        var peer = AddPeer(0x41, true, new GossipBlockHeightRange(0, uint.MaxValue));
        await BaselineAsync();
        _syncManager.Raise(m => m.FilterReceived += null,
                           new GossipFilterReceivedEventArgs(peer, _filters[peer]));

        for (var i = 1; i <= 4; i++)
        {
            await TickAfterAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(i, peer.Sent.Count);
        }

        Assert.Equal([MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2,
                      MessageTypes.ChannelUpdate2, MessageTypes.NodeAnnouncement2], peer.Sent.Select(m => m.Type));
        await FlushAllAsync();
        Assert.Equal(4, peer.Sent.Count);
    }

    [Fact]
    public async Task Given_BothProtocolBacklogs_When_Paced_Then_TheyShareOneWireMessageBudget()
    {
        _relay.Dispose();
        _relay = CreateRelay(null, o => o.BacklogMessagesPerSecond = 1);
        _graph.AddSignedChannel(new ShortChannelId(700, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        AddV2Channel();
        var peer = AddPeer(0x41, true, new GossipBlockHeightRange(0, uint.MaxValue));
        await BaselineAsync();
        _syncManager.Raise(m => m.FilterReceived += null,
                           new GossipFilterReceivedEventArgs(peer, _filters[peer]));

        for (var i = 1; i <= 7; i++)
        {
            await TickAfterAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(i, peer.Sent.Count);
        }

        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
                      MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2,
                      MessageTypes.ChannelUpdate2, MessageTypes.NodeAnnouncement2], peer.Sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Given_TheLastV1BacklogPassFillsTheOutbox_When_ItDrains_Then_TheV2BacklogResumesWithoutV1Duplicates()
    {
        var sender = new BlockingSender(3);
        _relay.Dispose();
        _relay = CreateRelay(null, sender: sender);
        _graph.AddSignedChannel(new ShortChannelId(700, 1, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        AddV2Channel();
        var peer = AddPeer(0x41, true, new GossipBlockHeightRange(0, uint.MaxValue));
        await BaselineAsync();
        _syncManager.Raise(m => m.FilterReceived += null,
                           new GossipFilterReceivedEventArgs(peer, _filters[peer]));

        await TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, peer.Sent.Count);
        Assert.Equal(1, _relay.GetStatus().PausedConnections);
        await TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(4, sender.Attempts);
        sender.Blocked = false;
        await TickAfterAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
                      MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2,
                      MessageTypes.ChannelUpdate2, MessageTypes.NodeAnnouncement2], peer.Sent.Select(m => m.Type));
        Assert.Equal(0, _relay.GetStatus().PausedConnections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AFullV2Outbox_When_ItDrainsOrStalls_Then_TheBacklogResumesOrIsDiscarded(bool stall)
    {
        var sender = new BlockingSender();
        _relay.Dispose();
        _relay = CreateRelay(null, o => o.RelayStallTimeout = TimeSpan.FromSeconds(5), sender);
        AddV2Channel();
        var peer = AddPeer(0x41, true, new GossipBlockHeightRange(0, uint.MaxValue));
        await BaselineAsync();
        _syncManager.Raise(m => m.FilterReceived += null,
                           new GossipFilterReceivedEventArgs(peer, _filters[peer]));
        await TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _relay.GetStatus().PausedConnections);
        Assert.Equal(1, sender.Attempts);
        await TickAfterAsync(TimeSpan.FromSeconds(stall ? 6 : 1));
        Assert.Equal(1, sender.Attempts);

        sender.Blocked = false;
        await TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(stall ? 0 : 4, peer.Sent.Count);
        Assert.Equal(0, _relay.GetStatus().PausedConnections);
    }

    [Fact]
    public async Task Given_AV2PendingFlushBlockedByAFullOutbox_When_ItStalls_Then_ItsPendingMessagesAreDropped()
    {
        var sender = new BlockingSender();
        _relay.Dispose();
        _relay = CreateRelay(null, o => o.RelayStallTimeout = TimeSpan.FromSeconds(5), sender);
        var peer = AddPeer(0x41, true, new GossipBlockHeightRange(0, uint.MaxValue));
        await BaselineAsync();
        AddV2Channel();
        await FlushAllAsync();
        Assert.True(_relay.GetStatus().PendingMessages > 0);
        await TickAfterAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(0, _relay.GetStatus().PendingMessages);

        sender.Blocked = false;
        await FlushAllAsync();
        Assert.Empty(peer.Sent);
    }

    private sealed class BlockingSender(int successfulBeforeBlock = 0) : IGossipPeerSender
    {
        public bool Blocked { get; set; } = true;
        public int Attempts { get; private set; }

        public async ValueTask<GossipEnqueueResult> SendAsync(GossipPeer peer, IMessage message, int size)
        {
            Attempts++;
            if (Blocked && Attempts > successfulBeforeBlock)
                return GossipEnqueueResult.Full;
            await peer.Service.SendGossipMessageAsync(message);
            return GossipEnqueueResult.Queued;
        }

        public GossipOutboxDepth? GetDepth(GossipPeer peer) =>
            new(Blocked ? 1 : 0, 0, 1, 0, 0);
    }

    [Fact]
    public async Task Given_OurOwnV2Gossip_When_Flushed_Then_EveryGossipV2PeerGetsItAndAV1PeerNone()
    {
        // Arrange: no filter needed for our own gossip (B7-Q-05)
        var v2Peer = AddPeer(0x41, gossipV2: true, null);
        var v1Peer = AddPeer(0x43, gossipV2: false, null);
        var announcement = Announcement();
        var update = GossipV2TestSigner.SignedChannelUpdate2(s_chain, s_scid, 0, 2_500, Node1());
        var node = GossipV2TestSigner.SignedNodeAnnouncement2(s_carol, 2_500);

        // Act: queued in the reverse order
        _relay.EnqueueOwnNodeAnnouncement2(node);
        _relay.EnqueueOwnChannelUpdate2(update);
        _relay.EnqueueOwnChannelAnnouncement2(announcement);
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([MessageTypes.ChannelAnnouncement2, MessageTypes.ChannelUpdate2, MessageTypes.NodeAnnouncement2],
                     v2Peer.Sent.Select(m => m.Type));
        Assert.Empty(v1Peer.Sent);
    }

    [Fact]
    public async Task Given_OurOwnAnnouncement2WithoutAnUpdate_When_Flushed_Then_NothingGoesOut()
    {
        // Arrange
        var v2Peer = AddPeer(0x41, gossipV2: true, null);
        _relay.EnqueueOwnChannelAnnouncement2(Announcement());
        _relay.EnqueueOwnNodeAnnouncement2(GossipV2TestSigner.SignedNodeAnnouncement2(s_carol, 2_500));

        // Act
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert: BOLT 7 never sends an announcement without its update, nor a node before its channel
        Assert.Empty(v2Peer.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AChannelOfOurs_When_ThePeersUpdate2Changes_Then_ItIsRelayedWithoutOurAnnouncement(
        bool asBacklog)
    {
        // Arrange (NL-1145): we are Carol, an end of the channel; Dave's channel_update_2 of it reached our graph
        _relay.Dispose();
        _relay = CreateRelay(s_carol);
        var peer = AddPeer(0x41, gossipV2: true, new GossipBlockHeightRange(0, uint.MaxValue));
        if (!asBacklog)
            await BaselineAsync();
        var (_, update1, update2, _) = AddV2Channel();
        var daves = Node1() == s_dave ? update1 : update2;
        if (asBacklog)
        {
            await BaselineAsync();
            _syncManager.Raise(m => m.FilterReceived += null,
                               new GossipFilterReceivedEventArgs(peer, _filters[peer]));
        }

        // Act
        await FlushAllAsync();

        // Assert: Dave's update goes out as BOLT 7's would; our 267, our 271 and our 269 are the own path's
        var sent = Assert.Single(peer.Sent);
        Assert.Equal(daves.GetBytes(), Assert.IsType<ChannelUpdate2Message>(sent).Payload.GetBytes());
    }

    private static ChannelAnnouncement2Payload Announcement() =>
        GossipV2TestSigner.SignedChannelAnnouncement2(s_chain, s_scid, 1_000_000, s_carol, s_dave,
                                                      new GossipV2TestKey(71), new GossipV2TestKey(72),
                                                      GraphTestKit.TxIdFor(s_scid));

    private static GossipV2TestKey Node1() =>
        ((ReadOnlySpan<byte>)s_carol.PubKey).SequenceCompareTo(s_dave.PubKey) < 0 ? s_carol : s_dave;

    /// <summary>A verified v2-only channel with both updates (blocks 2,500 and 2,501) and Carol's announcement.</summary>
    private (ChannelAnnouncement2Payload, ChannelUpdate2Payload, ChannelUpdate2Payload, NodeAnnouncement2Payload)
        AddV2Channel()
    {
        var announcement = Announcement();
        Assert.True(_graph.Store.TryAddChannel(
                        new GraphChannel(s_scid, announcement.NodeId1, announcement.NodeId2,
                                         announcement.BitcoinKey1, announcement.BitcoinKey2, 1_000_000)
                        {
                            Versions = GraphGossipVersions.V2,
                            RawAnnouncement2 = announcement.GetBytes()
                        }));
        var node1 = Node1();
        var node2 = node1 == s_carol ? s_dave : s_carol;
        var update1 = GossipV2TestSigner.SignedChannelUpdate2(s_chain, s_scid, 0, 2_500, node1);
        var update2 = GossipV2TestSigner.SignedChannelUpdate2(s_chain, s_scid, 1, 2_501, node2);
        foreach (var update in (ChannelUpdate2Payload[])[update1, update2])
            Assert.True(_graph.Store.TryApplyPolicy(s_scid, GraphPolicy.FromChannelUpdate2(update, 1_000_000_000)
                                                                with
            { RawUpdate = update.GetBytes() }));
        var node = GossipV2TestSigner.SignedNodeAnnouncement2(s_carol, 2_500);
        Assert.True(_graph.Store.TryApplyNode2(GraphNode.FromNodeAnnouncement2(node, node.GetBytes())));
        return (announcement, update1, update2, node);
    }

    private FakeGossipPeer AddPeer(byte seed, bool gossipV2, GossipBlockHeightRange? range)
    {
        var peer = new FakeGossipPeer(seed, gossipV2: gossipV2);
        _peers.Add(new GossipPeer(peer.PeerPubKey, peer));
        _filters[peer] = new GossipTimestampFilter(0, uint.MaxValue);
        if (range is { } r)
            _ranges[peer] = r;
        return peer;
    }

    private Task<int> BaselineAsync() => _relay.RelayTickAsync(TestContext.Current.CancellationToken);

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
}