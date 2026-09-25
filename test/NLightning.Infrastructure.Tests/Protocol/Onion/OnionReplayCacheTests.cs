namespace NLightning.Infrastructure.Tests.Protocol.Onion;

using Infrastructure.Protocol.Onion;

public class OnionReplayCacheTests
{
    private static byte[] Hmac(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    [Fact]
    public void Given_NewHmac_When_Adding_Then_ReturnsTrue()
    {
        // Arrange
        var cache = new OnionReplayCache();

        // Act
        var added = cache.TryAdd(Hmac(0x01));

        // Assert
        Assert.True(added);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Given_SameHmacTwice_When_Adding_Then_SecondAddReturnsFalse()
    {
        // Arrange
        var cache = new OnionReplayCache();
        cache.TryAdd(Hmac(0x01));

        // Act
        var addedAgain = cache.TryAdd(Hmac(0x01));

        // Assert
        Assert.False(addedAgain);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Given_FullCache_When_Adding_Then_OldestHmacIsEvicted()
    {
        // Arrange
        var cache = new OnionReplayCache(2);
        cache.TryAdd(Hmac(0x01));
        cache.TryAdd(Hmac(0x02));

        // Act
        var addedThird = cache.TryAdd(Hmac(0x03));

        // Assert
        Assert.True(addedThird);
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryAdd(Hmac(0x02)));
        Assert.False(cache.TryAdd(Hmac(0x03)));
        Assert.True(cache.TryAdd(Hmac(0x01)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_WrongLengthHmac_When_Adding_Then_ThrowsArgumentException(int length)
    {
        // Arrange
        var cache = new OnionReplayCache();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => cache.TryAdd(new byte[length]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Given_NonPositiveCapacity_When_Creating_Then_ThrowsArgumentOutOfRangeException(int capacity)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnionReplayCache(capacity));
    }

    [Fact]
    public async Task Given_ConcurrentAddsOfSameHmac_When_Adding_Then_ExactlyOneSucceeds()
    {
        // Arrange
        var cache = new OnionReplayCache();
        var hmac = Hmac(0x42);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 64)
                                                   .Select(_ => Task.Run(() => cache.TryAdd(hmac), ct)));

        // Assert
        Assert.Single(results, added => added);
    }

    [Fact]
    public async Task Given_ConcurrentAddsBeyondCapacity_When_Adding_Then_CountNeverExceedsCapacity()
    {
        // Arrange
        const int capacity = 16;
        var cache = new OnionReplayCache(capacity);
        var ct = TestContext.Current.CancellationToken;

        // Act
        await Task.WhenAll(Enumerable.Range(0, 256).Select(i => Task.Run(() =>
        {
            var hmac = new byte[32];
            BitConverter.TryWriteBytes(hmac, i);
            cache.TryAdd(hmac);
        }, ct)));

        // Assert
        Assert.Equal(capacity, cache.Count);
    }
}