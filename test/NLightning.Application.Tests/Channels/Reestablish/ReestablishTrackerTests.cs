namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Interfaces;
using Application.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Handlers;

public class ReestablishTrackerTests
{
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x21, 32).ToArray());
    private static readonly ChannelId s_otherChannel = new(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private static readonly CompactPubKey s_peer = NormalOperationTestContext.Point(0x0A);
    private static readonly CompactPubKey s_otherPeer = NormalOperationTestContext.Point(0x0B);

    [Fact]
    public void Given_UnknownChannel_When_Asked_Then_NotReestablished()
    {
        // Arrange
        var tracker = new ReestablishTracker();

        // Act / Assert
        Assert.False(tracker.IsReestablished(s_channel));
        Assert.Equal(ReestablishStatus.Awaiting, tracker.GetStatus(s_channel));
        Assert.False(tracker.TryMarkReestablished(s_channel));
    }

    [Fact]
    public void Given_OursSent_When_ThePeersArrives_Then_Reestablished()
    {
        // Arrange
        var tracker = new ReestablishTracker();
        tracker.MarkSent(s_channel, s_peer);

        // Act
        var marked = tracker.TryMarkReestablished(s_channel);

        // Assert
        Assert.True(marked);
        Assert.True(tracker.IsReestablished(s_channel));
    }

    [Fact]
    public void Given_OursSent_When_TheConnectionIsReplacedBeforeThePeersIsHandled_Then_NotReestablished()
    {
        // Arrange - the race the compare-and-set closes: reset in between
        var tracker = new ReestablishTracker();
        tracker.MarkSent(s_channel, s_peer);
        tracker.ResetPeer(s_peer);

        // Act
        var marked = tracker.TryMarkReestablished(s_channel);

        // Assert
        Assert.False(marked);
        Assert.False(tracker.IsReestablished(s_channel));
    }

    [Fact]
    public void Given_OpenedOnThisConnection_When_Asked_Then_UsableButOurReestablishIsStillOwed()
    {
        // Arrange
        var tracker = new ReestablishTracker();

        // Act
        tracker.MarkOpened(s_channel, s_peer);

        // Assert - updates may flow, and the peer's channel_reestablish is still answered
        Assert.True(tracker.IsReestablished(s_channel));
        Assert.Equal(ReestablishStatus.Awaiting, tracker.GetStatus(s_channel));
    }

    [Fact]
    public void Given_OursSent_When_TheChannelOpensAndThePeersArrives_Then_ItCompletesAndStaysUsable()
    {
        // Arrange
        var tracker = new ReestablishTracker();
        tracker.MarkSent(s_channel, s_peer);

        // Act
        tracker.MarkOpened(s_channel, s_peer);
        var marked = tracker.TryMarkReestablished(s_channel);

        // Assert - opening kept "ours sent", so the exchange still completes
        Assert.True(marked);
        Assert.Equal(ReestablishStatus.Reestablished, tracker.GetStatus(s_channel));
        Assert.True(tracker.IsReestablished(s_channel));
    }

    [Fact]
    public void Given_ChannelsOfTwoPeers_When_OnePeerResets_Then_OnlyItsChannelsAreForgotten()
    {
        // Arrange
        var tracker = new ReestablishTracker();
        tracker.MarkOpened(s_channel, s_peer);
        tracker.MarkOpened(s_otherChannel, s_otherPeer);

        // Act
        tracker.ResetPeer(s_peer);

        // Assert
        Assert.False(tracker.IsReestablished(s_channel));
        Assert.True(tracker.IsReestablished(s_otherChannel));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Given_GatedProbe_When_Asked_Then_AliveOnlyWhenReestablishedAndTheLinkIsUp(bool reestablished,
        bool linkUp, bool expected)
    {
        // Arrange
        var tracker = new ReestablishTracker();
        if (reestablished)
            tracker.MarkOpened(s_channel, s_peer);
        var inner = new Mock<IPeerLivenessProbe>();
        inner.Setup(p => p.IsAliveAsync(s_channel, s_peer, It.IsAny<CancellationToken>())).ReturnsAsync(linkUp);
        var probe = new ReestablishGatedLivenessProbe(inner.Object, tracker);

        // Act
        var alive = await probe.IsAliveAsync(s_channel, s_peer, TestContext.Current.CancellationToken);
        probe.MarkLinkUp(s_channel, s_peer);

        // Assert
        Assert.Equal(expected, alive);
        inner.Verify(p => p.MarkLinkUp(s_channel, s_peer), Times.Once);
    }
}