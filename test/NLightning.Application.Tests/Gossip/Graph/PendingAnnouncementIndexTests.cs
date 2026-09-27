namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;

/// <summary>NL-406: the bounded, expiring index of announcements that wait for their first update.</summary>
public class PendingAnnouncementIndexTests
{
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);

    [Fact]
    public void Given_AFullIndex_When_AnotherChannelIsAdded_Then_TheOldestIsEvicted()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(2, TimeSpan.FromDays(14), clock);
        Assert.False(index.AddOrReplace(Entry(1, clock)));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.False(index.AddOrReplace(Entry(2, clock)));

        // Act
        var evicted = index.AddOrReplace(Entry(3, clock));

        // Assert
        Assert.True(evicted);
        Assert.Equal(2, index.Count);
        Assert.False(index.Contains(Scid(1)));
        Assert.True(index.Contains(Scid(2)));
        Assert.True(index.Contains(Scid(3)));
    }

    [Fact]
    public void Given_AFullIndex_When_AKeptChannelIsReplaced_Then_NothingIsEvictedAndItBecomesTheNewest()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(2, TimeSpan.FromDays(14), clock);
        index.AddOrReplace(Entry(1, clock));
        index.AddOrReplace(Entry(2, clock));

        // Act
        var replaced = index.AddOrReplace(Entry(1, clock));
        var evicted = index.AddOrReplace(Entry(3, clock));

        // Assert: the replaced entry moved behind entry 2, which went first
        Assert.False(replaced);
        Assert.True(evicted);
        Assert.True(index.Contains(Scid(1)));
        Assert.False(index.Contains(Scid(2)));
    }

    [Fact]
    public void Given_AnExpiredAndAFullIndex_When_AnotherIsAdded_Then_TheExpiredOneMakesRoomWithoutAnEviction()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(2, TimeSpan.FromDays(14), clock);
        index.AddOrReplace(Entry(1, clock));
        clock.Now += TimeSpan.FromDays(10);
        index.AddOrReplace(Entry(2, clock));
        clock.Now += TimeSpan.FromDays(5);

        // Act
        var evicted = index.AddOrReplace(Entry(3, clock));

        // Assert
        Assert.False(evicted);
        Assert.False(index.Contains(Scid(1)));
        Assert.True(index.Contains(Scid(2)));
        Assert.True(index.Contains(Scid(3)));
    }

    [Fact]
    public void Given_EntriesOfDifferentAges_When_Pruned_Then_OnlyThoseOlderThanTheTtlGo()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(10, TimeSpan.FromHours(1), clock);
        index.AddOrReplace(Entry(1, clock));
        clock.Now += TimeSpan.FromMinutes(30);
        index.AddOrReplace(Entry(2, clock));
        clock.Now += TimeSpan.FromMinutes(31);

        // Act
        var expiredButCounted = index.Count;
        var lookup = index.TryGet(Scid(1), out _);
        var pruned = index.PruneExpired();

        // Assert
        Assert.Equal(2, expiredButCounted);
        Assert.False(lookup);
        Assert.Equal(1, pruned);
        Assert.Equal(1, index.Count);
        Assert.True(index.TryGet(Scid(2), out var kept));
        Assert.Equal(Scid(2), kept.ShortChannelId);
    }

    [Fact]
    public void Given_AReplacedEntry_When_TheOldInstanceIsRemoved_Then_TheNewOneStays()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(10, TimeSpan.FromDays(14), clock);
        var first = Entry(1, clock);
        index.AddOrReplace(first);
        var second = Entry(1, clock);
        index.AddOrReplace(second);

        // Act
        var removedOld = index.Remove(first);
        var removedNew = index.Remove(second);

        // Assert
        Assert.False(removedOld);
        Assert.True(removedNew);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Given_AnEntry_When_ReadAsAChannel_Then_ItIsUncheckedWithoutCapacity()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var entry = Entry(1, clock);

        // Act
        var channel = entry.ToUncheckedChannel();

        // Assert
        Assert.Equal(Scid(1), channel.ShortChannelId);
        Assert.Null(channel.CapacitySat);
        Assert.False(channel.IsChainChecked);
        Assert.Equal(entry.Raw, channel.RawAnnouncement.ToArray());
    }

    private static ShortChannelId Scid(uint height) => new(height, 1, 0);

    private static PendingAnnouncement Entry(uint height, SettableTimeProvider clock)
    {
        var payload = GraphTestKit.SignedChannelAnnouncement(Scid(height), s_alice, s_bob, new TestGossipKey(11),
                                                             new TestGossipKey(12)).Payload;
        return new PendingAnnouncement(payload.ShortChannelId, payload.GetBytes(), s_alice.PubKey,
                                       clock.GetUtcNow());
    }
}