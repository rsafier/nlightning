namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;

/// <summary>BOLT 7 plan §3.8: the token buckets, the node interval and the misbehaviour window on their own.</summary>
public class GossipRateLimiterTests
{
    private static readonly ShortChannelId s_scid = new(1, 2, 3);
    private static readonly TestGossipKey s_node = new(1);

    [Fact]
    public void Given_ABurstOfFour_When_SpentAndTimePasses_Then_OneTokenComesBackPerInterval()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var limiter = new GossipRateLimiter(TimeSpan.FromSeconds(60), 4, TimeSpan.FromMinutes(10), clock);

        // Act
        var burst = Enumerable.Range(0, 5).Select(_ => limiter.TryAcquireUpdate(s_scid, 0)).ToList();
        var otherDirection = limiter.TryAcquireUpdate(s_scid, 1);
        clock.Now += TimeSpan.FromSeconds(30);
        var halfway = limiter.CanAcceptUpdate(s_scid, 0);
        clock.Now += TimeSpan.FromSeconds(30);
        var afterAMinute = limiter.TryAcquireUpdate(s_scid, 0);
        clock.Now += TimeSpan.FromHours(1);
        var refilled = Enumerable.Range(0, 5).Select(_ => limiter.TryAcquireUpdate(s_scid, 0)).ToList();

        // Assert
        Assert.Equal([true, true, true, true, false], burst);
        Assert.True(otherDirection);
        Assert.False(halfway);
        Assert.True(afterAMinute);
        Assert.Equal([true, true, true, true, false], refilled);
    }

    [Fact]
    public void Given_ANodeInterval_When_AnnouncementsCome_Then_OnePerInterval()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var limiter = new GossipRateLimiter(TimeSpan.FromSeconds(60), 4, TimeSpan.FromMinutes(10), clock);

        // Act
        var first = limiter.TryAcquireNodeAnnouncement(s_node.PubKey);
        var second = limiter.TryAcquireNodeAnnouncement(s_node.PubKey);
        clock.Now += TimeSpan.FromMinutes(10);
        var canAfter = limiter.CanAcceptNodeAnnouncement(s_node.PubKey);
        var third = limiter.TryAcquireNodeAnnouncement(s_node.PubKey);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(canAfter);
        Assert.True(third);
    }

    [Fact]
    public void Given_IdleEntries_When_Pruned_Then_OnlyTheLimitingOnesStay()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var limiter = new GossipRateLimiter(TimeSpan.FromSeconds(60), 4, TimeSpan.FromMinutes(10), clock);
        limiter.TryAcquireUpdate(s_scid, 0);
        limiter.TryAcquireNodeAnnouncement(s_node.PubKey);
        clock.Now += TimeSpan.FromMinutes(5);
        limiter.TryAcquireUpdate(new ShortChannelId(9, 9, 9), 1);

        // Act
        var removed = limiter.Prune();

        // Assert: the first bucket is full again, the node interval and the new bucket still limit
        Assert.Equal(1, removed);
        Assert.Equal(2, limiter.Count);
    }

    [Fact]
    public void Given_LimitsTurnedOff_When_Asked_Then_EverythingPasses()
    {
        // Arrange
        var limiter = new GossipRateLimiter(TimeSpan.Zero, 1, TimeSpan.Zero);

        // Act / Assert
        Assert.All(Enumerable.Range(0, 10), _ => Assert.True(limiter.TryAcquireUpdate(s_scid, 0)));
        Assert.All(Enumerable.Range(0, 10), _ => Assert.True(limiter.TryAcquireNodeAnnouncement(s_node.PubKey)));
        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public void Given_AMisbehaviourWindow_When_EntriesAgeOut_Then_TheyNoLongerCount()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var tracker = new GossipMisbehaviourTracker(3, TimeSpan.FromMinutes(10), clock);

        // Act
        var first = tracker.Record(s_node.PubKey);
        clock.Now += TimeSpan.FromMinutes(6);
        var second = tracker.Record(s_node.PubKey);
        clock.Now += TimeSpan.FromMinutes(5);
        var thirdAfterTheFirstAgedOut = tracker.Record(s_node.PubKey);
        var fourth = tracker.Record(s_node.PubKey);
        var scoreAfterReport = tracker.GetScore(s_node.PubKey);
        tracker.Record(s_node.PubKey);
        clock.Now += TimeSpan.FromMinutes(10);
        var pruned = tracker.Prune();

        // Assert
        Assert.False(first);
        Assert.False(second);
        Assert.False(thirdAfterTheFirstAgedOut);
        Assert.True(fourth);
        Assert.Equal(0, scoreAfterReport);
        Assert.Equal(1, pruned);
        Assert.Equal(0, tracker.Count);
        Assert.False(new GossipMisbehaviourTracker(0, TimeSpan.FromMinutes(1), clock).Record(s_node.PubKey));
    }
}