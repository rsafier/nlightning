namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Relay;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Graph;
using Sync;

/// <summary>BOLT 7 plan G3-T3: who sent us which gossip version (origin suppression) and the recording ingress.</summary>
public class GossipOriginTrackerTests
{
    private static readonly ShortChannelId s_scid = new(500, 1, 0);

    [Fact]
    public void Given_AnUpdateFromAPeer_When_Asked_Then_OnlyThatVersionAndThatPeerAreTheOrigin()
    {
        // Arrange
        var tracker = new GossipOriginTracker();
        var peer = new TestGossipKey(0x41).PubKey;
        var other = new TestGossipKey(0x42).PubKey;
        tracker.Record(GraphTestKit.SignedChannelUpdate(s_scid, SyncTestGraph.NodeA, 1, 100), peer);

        // Act / Assert
        Assert.True(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 1, 100), peer));
        Assert.False(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 1, 101), peer));
        Assert.False(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 0, 100), peer));
        Assert.False(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 1, 100), other));
    }

    [Fact]
    public void Given_TheSameVersionFromTwoPeers_When_Asked_Then_BothAreOrigins()
    {
        // Arrange
        var tracker = new GossipOriginTracker();
        var first = new TestGossipKey(0x41).PubKey;
        var second = new TestGossipKey(0x42).PubKey;
        var announcement = GraphTestKit.SignedNodeAnnouncement(SyncTestGraph.NodeA, 100);

        // Act
        tracker.Record(announcement, first);
        tracker.Record(announcement, second);
        tracker.Record(announcement, second);

        // Assert
        var key = GossipMessageKey.NodeAnnouncement(SyncTestGraph.NodeA.PubKey, 100);
        Assert.True(tracker.IsOrigin(key, first));
        Assert.True(tracker.IsOrigin(key, second));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Given_AFullTracker_When_ANewVersionIsRecorded_Then_TheOldestIsForgotten()
    {
        // Arrange
        var tracker = new GossipOriginTracker(2);
        var peer = new TestGossipKey(0x41).PubKey;

        // Act
        for (uint timestamp = 1; timestamp <= 3; timestamp++)
            tracker.Record(GossipMessageKey.ChannelUpdate(s_scid, 0, timestamp), peer);

        // Assert
        Assert.Equal(2, tracker.Count);
        Assert.False(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 0, 1), peer));
        Assert.True(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 0, 3), peer));
    }

    [Fact]
    public void Given_ANonGossipMessage_When_Keyed_Then_ThereIsNoKey()
    {
        // Act / Assert
        Assert.Null(GossipOriginTracker.KeyOf(new Mock<IMessage>().Object));
    }

    [Fact]
    public void Given_TheRecordingIngress_When_APeerHandsOverGossip_Then_ItIsRecordedAndPassedOn()
    {
        // Arrange
        var tracker = new GossipOriginTracker();
        var inner = new Mock<IGossipIngress>();
        inner.SetupGet(i => i.IsEnabled).Returns(true);
        inner.Setup(i => i.TryEnqueue(It.IsAny<IPeerService>(), It.IsAny<IMessage>())).Returns(true);
        var peer = new FakeGossipPeer(0x41);
        var update = GraphTestKit.SignedChannelUpdate(s_scid, SyncTestGraph.NodeA, 0, 100);
        var ingress = new OriginTrackingGossipIngress(inner.Object, tracker);

        // Act
        var queued = ingress.TryEnqueue(peer, update);

        // Assert
        Assert.True(queued);
        Assert.True(ingress.IsEnabled);
        inner.Verify(i => i.TryEnqueue(peer, update), Times.Once);
        Assert.True(tracker.IsOrigin(GossipMessageKey.ChannelUpdate(s_scid, 0, 100), peer.PeerPubKey));
    }

    [Fact]
    public void Given_TheGraphDisabled_When_APeerHandsOverGossip_Then_NothingIsRecorded()
    {
        // Arrange
        var tracker = new GossipOriginTracker();
        var inner = new Mock<IGossipIngress>();
        var ingress = new OriginTrackingGossipIngress(inner.Object, tracker);

        // Act
        ingress.TryEnqueue(new FakeGossipPeer(0x41), GraphTestKit.SignedChannelUpdate(s_scid, SyncTestGraph.NodeA, 0, 1));

        // Assert
        Assert.Equal(0, tracker.Count);
    }
}