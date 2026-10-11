namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;

public class GossipCacheTests
{
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    [Fact]
    public void Given_RecentMessageCache_When_TheSameBytesAreAddedTwice_Then_TheSecondIsKnown()
    {
        // Arrange
        var cache = new RecentMessageCache(10);

        // Act
        var first = cache.Add(258, [1, 2, 3]);
        var second = cache.Add(258, [1, 2, 3]);
        var otherType = cache.Add(257, [1, 2, 3]);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(otherType);
        Assert.True(cache.Contains(258, [1, 2, 3]));
    }

    [Fact]
    public void Given_FullRecentMessageCache_When_OneMoreIsAdded_Then_TheOldestIsForgotten()
    {
        // Arrange
        var cache = new RecentMessageCache(2);
        cache.Add(258, [1]);
        cache.Add(258, [2]);

        // Act
        cache.Add(258, [3]);

        // Assert
        Assert.Equal(2, cache.Count);
        Assert.False(cache.Contains(258, [1]));
        Assert.True(cache.Contains(258, [3]));
    }

    [Fact]
    public void Given_OrphanUpdates_When_TheirChannelIsTaken_Then_OnlyTheNewestPerDirectionComesBack()
    {
        // Arrange
        var cache = new OrphanUpdateCache(10, TimeSpan.FromMinutes(10), new SettableTimeProvider(GraphTestKit.DefaultNow));
        var scid = new ShortChannelId(110, 1, 0);
        var older = GraphTestKit.SignedChannelUpdate(scid, s_alice, 0, s_now - 10);
        var newer = GraphTestKit.SignedChannelUpdate(scid, s_alice, 0, s_now);
        var otherDirection = GraphTestKit.SignedChannelUpdate(scid, s_alice, 1, s_now);

        // Act
        Assert.True(cache.AddUpdate(older, null));
        Assert.True(cache.AddUpdate(newer, null));
        Assert.False(cache.AddUpdate(older, null));
        Assert.True(cache.AddUpdate(otherDirection, null));
        var taken = cache.TakeUpdates(scid);

        // Assert
        Assert.Equal(2, taken.Count);
        Assert.Contains(taken, e => ReferenceEquals(e.Message, newer));
        Assert.Contains(taken, e => ReferenceEquals(e.Message, otherDirection));
        Assert.Empty(cache.TakeUpdates(scid));
    }

    [Fact]
    public void Given_OrphansOlderThanTheTtl_When_Taken_Then_NothingComesBack()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var cache = new OrphanUpdateCache(10, TimeSpan.FromMinutes(10), clock);
        var scid = new ShortChannelId(110, 1, 0);
        cache.AddUpdate(GraphTestKit.SignedChannelUpdate(scid, s_alice, 0, s_now), null);
        cache.AddNodeAnnouncement(GraphTestKit.SignedNodeAnnouncement(s_alice, s_now), null);

        // Act
        clock.Now += TimeSpan.FromMinutes(11);

        // Assert
        Assert.Empty(cache.TakeUpdates(scid));
        Assert.Null(cache.TakeNodeAnnouncement(s_alice.PubKey));
    }

    [Fact]
    public void Given_FullOrphanCache_When_AnotherArrives_Then_ItIsRefusedUntilOldOnesExpire()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var cache = new OrphanUpdateCache(2, TimeSpan.FromMinutes(10), clock);
        cache.AddUpdate(GraphTestKit.SignedChannelUpdate(new ShortChannelId(1, 1, 0), s_alice, 0, s_now), null);
        cache.AddUpdate(GraphTestKit.SignedChannelUpdate(new ShortChannelId(2, 1, 0), s_alice, 0, s_now), null);
        var third = GraphTestKit.SignedChannelUpdate(new ShortChannelId(3, 1, 0), s_alice, 0, s_now);

        // Act
        var whileFull = cache.AddUpdate(third, null);
        clock.Now += TimeSpan.FromMinutes(11);
        var afterExpiry = cache.AddUpdate(third, null);

        // Assert
        Assert.False(whileFull);
        Assert.True(afterExpiry);
        Assert.Equal(1, cache.Count);
    }
}