using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Memory;

using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-392: temporary channels of failed or abandoned opens leave <see cref="ChannelMemoryRepository"/>.
/// </summary>
public class ChannelMemoryRepositoryTests
{
    private static readonly CompactPubKey s_peerA = CreatePubKey(1);
    private static readonly CompactPubKey s_peerB = CreatePubKey(2);

    [Fact]
    public void Given_TemporaryChannelsOfTwoPeers_When_RemoveTemporaryChannels_Then_OnlyThatPeersAreRemoved()
    {
        // Arrange
        var repository = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance);
        var a1 = CreateTemporaryChannel(s_peerA, 1);
        var a2 = CreateTemporaryChannel(s_peerA, 2);
        var b1 = CreateTemporaryChannel(s_peerB, 3);
        repository.AddTemporaryChannel(s_peerA, a1);
        repository.AddTemporaryChannel(s_peerA, a2);
        repository.AddTemporaryChannel(s_peerB, b1);

        // Act
        var removed = repository.RemoveTemporaryChannels(s_peerA);

        // Assert
        Assert.Equal(2, removed.Count);
        Assert.Contains(a1.ChannelId, removed);
        Assert.Contains(a2.ChannelId, removed);
        Assert.False(repository.TryGetTemporaryChannel(s_peerA, a1.ChannelId, out _));
        Assert.False(repository.TryGetTemporaryChannelState(s_peerA, a2.ChannelId, out _));
        Assert.True(repository.TryGetTemporaryChannel(s_peerB, b1.ChannelId, out _));
    }

    [Fact]
    public void Given_ATemporaryChannelOlderThanTheTimeout_When_Looked_Up_Then_ItIsGone()
    {
        // Arrange
        var clock = new ManualClock();
        var repository = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance, clock)
        {
            TemporaryChannelTimeout = TimeSpan.FromMinutes(10)
        };
        var channel = CreateTemporaryChannel(s_peerA, 1);
        repository.AddTemporaryChannel(s_peerA, channel);

        // Act
        clock.Advance(TimeSpan.FromMinutes(9));
        var foundBefore = repository.TryGetTemporaryChannelState(s_peerA, channel.ChannelId, out var stateBefore);
        clock.Advance(TimeSpan.FromMinutes(1));
        var foundAfter = repository.TryGetTemporaryChannel(s_peerA, channel.ChannelId, out _);

        // Assert
        Assert.True(foundBefore);
        Assert.Equal(ChannelState.V1Opening, stateBefore);
        Assert.False(foundAfter);
        Assert.False(repository.TryGetTemporaryChannelState(s_peerA, channel.ChannelId, out _));
        Assert.Empty(repository.RemoveTemporaryChannels(s_peerA));
    }

    [Fact]
    public void Given_AnExpiredTemporaryChannel_When_AnotherOpenStarts_Then_TheExpiredOneIsDropped()
    {
        // Arrange (an abandoned open nobody looks up again must not stay in memory)
        var clock = new ManualClock();
        var repository = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance, clock);
        var abandoned = CreateTemporaryChannel(s_peerA, 1);
        repository.AddTemporaryChannel(s_peerA, abandoned);
        clock.Advance(ChannelMemoryRepository.DefaultTemporaryChannelTimeout);

        // Act
        repository.AddTemporaryChannel(s_peerB, CreateTemporaryChannel(s_peerB, 2));
        clock.Advance(-ChannelMemoryRepository.DefaultTemporaryChannelTimeout);

        // Assert: gone although the clock went back below the timeout
        Assert.False(repository.TryGetTemporaryChannel(s_peerA, abandoned.ChannelId, out _));
        Assert.Empty(repository.RemoveTemporaryChannels(s_peerA));
    }

    [Fact]
    public void Given_ARemovedTemporaryChannel_When_TheSameIdIsAddedAgain_Then_ItGetsAFreshTimeout()
    {
        // Arrange
        var clock = new ManualClock();
        var repository = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance, clock);
        var channel = CreateTemporaryChannel(s_peerA, 1);
        repository.AddTemporaryChannel(s_peerA, channel);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(repository.TryRemoveTemporaryChannel(s_peerA, channel.ChannelId));

        // Act
        repository.AddTemporaryChannel(s_peerA, channel);
        clock.Advance(TimeSpan.FromMinutes(5));

        // Assert
        Assert.True(repository.TryGetTemporaryChannel(s_peerA, channel.ChannelId, out _));
    }

    private static ChannelModel CreateTemporaryChannel(CompactPubKey peer, byte seed)
    {
        var channelId = new ChannelId(Enumerable.Repeat(seed, 32).ToArray());
        var keySet = new ChannelKeySetModel(0, peer, peer, peer, peer, peer, peer);
        return new ChannelModel(new ChannelParams(), channelId, null, null, true, null, null,
                                LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0, peer, 0,
                                ChannelState.V1Opening, ChannelVersion.V1);
    }

    private static CompactPubKey CreatePubKey(byte seed)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = seed;
        return new CompactPubKey(bytes);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}