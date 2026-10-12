namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;

/// <summary>NL-406: the bounded, expiring index of announcements that wait for their first update.</summary>
public class PendingAnnouncementIndexTests
{
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_mallory = new(55);

    [Fact]
    public void Given_AFullIndex_When_AnotherChannelIsAdded_Then_TheOldestIsEvicted()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(2, TimeSpan.FromDays(14), clock);
        Assert.Equal(PendingAddOutcome.Added, index.Add(Entry(1, clock)));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(PendingAddOutcome.Added, index.Add(Entry(2, clock)));

        // Act
        var evicted = index.Add(Entry(3, clock));

        // Assert
        Assert.Equal(PendingAddOutcome.AddedWithEviction, evicted);
        Assert.Equal(2, index.Count);
        Assert.False(index.Contains(Scid(1)));
        Assert.True(index.Contains(Scid(2)));
        Assert.True(index.Contains(Scid(3)));
    }

    [Fact]
    public void Given_AFullIndex_When_APeerReplacesItsOwnEntry_Then_NothingIsEvictedAndItBecomesTheNewest()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(2, TimeSpan.FromDays(14), clock);
        index.Add(Entry(1, clock));
        index.Add(Entry(2, clock));

        // Act
        var replaced = index.Add(Entry(1, clock));
        var evicted = index.Add(Entry(3, clock));

        // Assert: the replaced entry moved behind entry 2, which went first
        Assert.Equal(PendingAddOutcome.Added, replaced);
        Assert.Equal(PendingAddOutcome.AddedWithEviction, evicted);
        Assert.True(index.Contains(Scid(1)));
        Assert.False(index.Contains(Scid(2)));
    }

    [Fact]
    public void Given_AnExpiredAndAFullIndex_When_AnotherIsAdded_Then_TheExpiredOneMakesRoomWithoutAnEviction()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(2, TimeSpan.FromDays(14), clock);
        index.Add(Entry(1, clock));
        clock.Now += TimeSpan.FromDays(10);
        index.Add(Entry(2, clock));
        clock.Now += TimeSpan.FromDays(5);

        // Act
        var evicted = index.Add(Entry(3, clock));

        // Assert
        Assert.Equal(PendingAddOutcome.Added, evicted);
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
        index.Add(Entry(1, clock));
        clock.Now += TimeSpan.FromMinutes(30);
        index.Add(Entry(2, clock));
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
        index.Add(first);
        var second = Entry(1, clock);
        index.Add(second);

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

    [Fact]
    public void Given_DifferentAnnouncementsFromTwoPeers_When_Added_Then_BothWaitAsCandidatesOldestFirst()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(10, TimeSpan.FromDays(14), clock);
        var real = Entry(1, clock);
        clock.Now += TimeSpan.FromSeconds(1);
        var forged = Forged(1, s_mallory, clock);

        // Act
        index.Add(real);
        var second = index.Add(forged);

        // Assert
        Assert.Equal(PendingAddOutcome.Added, second);
        Assert.Equal([real, forged], index.GetCandidates(Scid(1)));
        Assert.True(index.ContainsRaw(Scid(1), real.Raw));
        Assert.True(index.ContainsRaw(Scid(1), forged.Raw));
        Assert.True(index.TryGet(Scid(1), out var oldest));
        Assert.Same(real, oldest);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Given_TheMostCandidatesOfOtherPeers_When_AnotherDifferentAnnouncementArrives_Then_ItIsRefusedAndTheFirstStay()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(100, TimeSpan.FromDays(14), clock);
        var real = Entry(1, clock);
        index.Add(real);
        for (var i = 1; i < PendingAnnouncementIndex.MaxCandidatesPerChannel; i++)
            Assert.Equal(PendingAddOutcome.Added, index.Add(Forged(1, new TestGossipKey((byte)(80 + i)), clock)));

        // Act
        var refused = index.Add(Forged(1, new TestGossipKey(99), clock));

        // Assert
        Assert.Equal(PendingAddOutcome.Refused, refused);
        Assert.Equal(PendingAnnouncementIndex.MaxCandidatesPerChannel, index.GetCandidates(Scid(1)).Count);
        Assert.Contains(real, index.GetCandidates(Scid(1)));
    }

    [Fact]
    public void Given_APeerWithACandidate_When_ItSendsAnotherAnnouncement_Then_ItReplacesOnlyItsOwn()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(10, TimeSpan.FromDays(14), clock);
        var real = Entry(1, clock);
        index.Add(real);
        index.Add(Forged(1, s_mallory, clock));

        // Act
        var again = Forged(1, s_mallory, clock, nodeSeed: 70);
        index.Add(again);

        // Assert
        Assert.Equal([real, again], index.GetCandidates(Scid(1)));
    }

    [Fact]
    public void Given_AFullIndexMostlyHeldByOnePeer_When_AnotherPeerAddsOne_Then_TheFloodersOldestMakesRoom()
    {
        // Arrange: the honest peer's entry is the oldest of all
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(4, TimeSpan.FromDays(14), clock);
        var honest = Entry(1, clock);
        index.Add(honest);
        for (uint height = 10; height < 13; height++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            index.Add(Forged(height, s_mallory, clock));
        }

        // Act
        clock.Now += TimeSpan.FromSeconds(1);
        var outcome = index.Add(Entry(2, clock));

        // Assert: mallory's oldest went, both honest entries stay
        Assert.Equal(PendingAddOutcome.AddedWithEviction, outcome);
        Assert.True(index.Contains(Scid(1)));
        Assert.True(index.Contains(Scid(2)));
        Assert.False(index.Contains(Scid(10)));
        Assert.True(index.Contains(Scid(11)));
        Assert.Equal(4, index.Count);
    }

    [Fact]
    public void Given_TwoCandidates_When_OneIsRemoved_Then_TheOtherStaysUntilTheChannelIsRemoved()
    {
        // Arrange
        var clock = new SettableTimeProvider(GraphTestKit.DefaultNow);
        var index = new PendingAnnouncementIndex(10, TimeSpan.FromDays(14), clock);
        var real = Entry(1, clock);
        var forged = Forged(1, s_mallory, clock);
        index.Add(real);
        index.Add(forged);

        // Act
        var removedForged = index.Remove(forged);
        var realLeft = index.GetCandidates(Scid(1));
        var removedChannel = index.Remove(Scid(1));

        // Assert
        Assert.True(removedForged);
        Assert.Equal([real], realLeft);
        Assert.True(removedChannel);
        Assert.Equal(0, index.Count);
        Assert.Empty(index.GetCandidates(Scid(1)));
    }

    private static ShortChannelId Scid(uint height) => new(height, 1, 0);

    private static PendingAnnouncement Forged(uint height, TestGossipKey origin, SettableTimeProvider clock,
                                              byte nodeSeed = 66)
    {
        var payload = GraphTestKit.SignedChannelAnnouncement(Scid(height), new TestGossipKey(nodeSeed),
                                                             new TestGossipKey((byte)(nodeSeed + 1)),
                                                             new TestGossipKey(76), new TestGossipKey(77)).Payload;
        return new PendingAnnouncement(payload.ShortChannelId, payload.GetBytes(), origin.PubKey, clock.GetUtcNow());
    }

    private static PendingAnnouncement Entry(uint height, SettableTimeProvider clock)
    {
        var payload = GraphTestKit.SignedChannelAnnouncement(Scid(height), s_alice, s_bob, new TestGossipKey(11),
                                                             new TestGossipKey(12)).Payload;
        return new PendingAnnouncement(payload.ShortChannelId, payload.GetBytes(), s_alice.PubKey,
                                       clock.GetUtcNow());
    }
}