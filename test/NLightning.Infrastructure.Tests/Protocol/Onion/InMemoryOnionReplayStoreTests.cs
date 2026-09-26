using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Infrastructure.Protocol.Onion;

/// <summary>
/// <see cref="InMemoryOnionReplayStore"/>: the cltv-expiring, HTLC-owned replay set (NL-078).
/// </summary>
public class InMemoryOnionReplayStoreTests
{
    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    [Fact]
    public async Task Given_ANewHmac_When_Added_Then_True()
    {
        // Arrange
        var store = new InMemoryOnionReplayStore();

        // Act
        var added = await store.TryAddAsync(Hmac(1), s_channelA, 0, 500, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(added);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Given_AnHmacOfAnotherHtlc_When_Added_Then_ItIsAReplay()
    {
        // Arrange
        var store = new InMemoryOnionReplayStore();
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 500, TestContext.Current.CancellationToken);

        // Act - same onion on another HTLC of the same channel, and on another channel
        var sameChannel = await store.TryAddAsync(Hmac(1), s_channelA, 1, 600, TestContext.Current.CancellationToken);
        var otherChannel = await store.TryAddAsync(Hmac(1), s_channelB, 0, 600, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(sameChannel);
        Assert.False(otherChannel);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Given_AnHmacOfTheSameHtlc_When_AddedAgain_Then_ItIsNotAReplay()
    {
        // Arrange - the switch re-processes a locked-in HTLC after a restart or a link-up
        var store = new InMemoryOnionReplayStore();
        await store.TryAddAsync(Hmac(1), s_channelA, 7, 500, TestContext.Current.CancellationToken);

        // Act
        var again = await store.TryAddAsync(Hmac(1), s_channelA, 7, 500, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(again);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Given_EntriesWithSeveralExpiries_When_Pruning_Then_OnlyThoseTheChainPassedAreForgotten()
    {
        // Arrange
        var store = new InMemoryOnionReplayStore();
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 100, TestContext.Current.CancellationToken);
        await store.TryAddAsync(Hmac(2), s_channelA, 1, 101, TestContext.Current.CancellationToken);
        await store.TryAddAsync(Hmac(3), s_channelA, 2, 200, TestContext.Current.CancellationToken);

        // Act - the chain is at 101: the entry expiring at 100 is past, the one at 101 is still kept
        var removed = await store.PruneAsync(101, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, removed);
        Assert.Equal(2, store.Count);
        Assert.True(await store.TryAddAsync(Hmac(1), s_channelB, 0, 300, TestContext.Current.CancellationToken));
        Assert.False(await store.TryAddAsync(Hmac(2), s_channelB, 0, 300, TestContext.Current.CancellationToken));
        Assert.False(await store.TryAddAsync(Hmac(3), s_channelB, 0, 300, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AFullStore_When_Adding_Then_TheEntryThatExpiresFirstIsDropped()
    {
        // Arrange
        var store = new InMemoryOnionReplayStore(2);
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 300, TestContext.Current.CancellationToken);
        await store.TryAddAsync(Hmac(2), s_channelA, 1, 100, TestContext.Current.CancellationToken);

        // Act
        await store.TryAddAsync(Hmac(3), s_channelA, 2, 200, TestContext.Current.CancellationToken);

        // Assert - HMAC 2 (expiry 100) went, HMAC 1 (older, but expiring later) is still a replay
        Assert.Equal(2, store.Count);
        Assert.False(await store.TryAddAsync(Hmac(1), s_channelB, 0, 400, TestContext.Current.CancellationToken));
        Assert.True(await store.TryAddAsync(Hmac(2), s_channelB, 0, 400, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ConcurrentAddsOfOneHmac_When_ByDifferentHtlcs_Then_ExactlyOneWins()
    {
        // Arrange
        var store = new InMemoryOnionReplayStore();

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(
                                                                             () => store.TryAddAsync(
                                                                                 Hmac(9), s_channelA, (ulong)i, 500,
                                                                                 TestContext.Current
                                                                                    .CancellationToken),
                                                                             TestContext.Current.CancellationToken)));

        // Assert
        Assert.Single(results, r => r);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Given_AnHmacOfTheWrongLength_When_Added_Then_ArgumentException(int length)
    {
        // Arrange
        var store = new InMemoryOnionReplayStore();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAddAsync(new byte[length], s_channelA, 0, 1,
                                                                            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Given_ANonPositiveCapacity_When_Constructing_Then_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryOnionReplayStore(capacity));
    }

    [Fact]
    public void Given_TheRegistration_When_Resolved_Then_OneSharedInMemoryStore()
    {
        // Arrange
        using var provider = new ServiceCollection().AddOnionReplayStore().BuildServiceProvider();

        // Act
        var store = provider.GetRequiredService<IOnionReplayStore>();

        // Assert
        Assert.IsType<InMemoryOnionReplayStore>(store);
        Assert.Same(store, provider.GetRequiredService<IOnionReplayStore>());
    }

    [Fact]
    public void Given_AStoreRegisteredFirst_When_AddingTheReplayStore_Then_ItIsKept()
    {
        // Arrange - a persistent store registered by the host wins
        var persistent = new Mock<IOnionReplayStore>().Object;
        var services = new ServiceCollection().AddSingleton(persistent);

        // Act
        using var provider = services.AddOnionReplayStore().BuildServiceProvider();

        // Assert
        Assert.Same(persistent, provider.GetRequiredService<IOnionReplayStore>());
    }

    private static byte[] Hmac(byte seed) => Enumerable.Repeat(seed, 32).ToArray();
}