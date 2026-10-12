namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;

public class TokenBucketRateLimiterTests
{
    [Fact]
    public void Given_FullBucket_When_ReservingPastTheBurst_Then_WaitsGrowByOneInterval()
    {
        // Arrange: 2 per second, burst 2
        var clock = new ManualTimeProvider();
        var limiter = new TokenBucketRateLimiter(2, clock);

        // Act
        var waits = Enumerable.Range(0, 4).Select(_ => limiter.Reserve()).ToList();

        // Assert
        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)], waits);
    }

    [Fact]
    public void Given_IdleLongerThanTheBurst_When_Reserving_Then_RefillIsCapped()
    {
        // Arrange: empty the bucket, then stay idle for 10 s
        var clock = new ManualTimeProvider();
        var limiter = new TokenBucketRateLimiter(2, clock);
        limiter.Reserve();
        limiter.Reserve();
        clock.Advance(TimeSpan.FromSeconds(10));

        // Act
        var waits = Enumerable.Range(0, 3).Select(_ => limiter.Reserve()).ToList();

        // Assert: only 2 tokens stored, not 20
        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)], waits);
    }

    [Fact]
    public async Task Given_EmptyBucket_When_WaitAsync_Then_CompletesWhenTheTokenIsDue()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var limiter = new TokenBucketRateLimiter(1, clock);
        var ct = TestContext.Current.CancellationToken;
        await limiter.WaitAsync(ct);

        // Act
        var wait = limiter.WaitAsync(ct);
        var pendingBefore = !wait.IsCompleted;
        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        Assert.True(pendingBefore);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public void Given_ZeroRate_When_Constructed_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(0, TimeProvider.System));
    }
}