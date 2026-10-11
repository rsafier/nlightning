namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Services;
using Domain.Channels.ValueObjects;

public class ChannelLockProviderTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Given_LockHeld_When_SameChannelAcquired_Then_WaitsUntilReleased()
    {
        // Arrange
        var provider = new ChannelLockProvider();
        var channelId = CreateChannelId(0x01);
        var first = await provider.AcquireAsync(channelId, TestContext.Current.CancellationToken);

        // Act
        var second = provider.AcquireAsync(channelId, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var completedWhileHeld = second.IsCompleted;
        first.Dispose();
        var secondLock = await second.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(completedWhileHeld);
        secondLock.Dispose();
        Assert.Equal(0, provider.ActiveLockCount);
    }

    [Fact]
    public async Task Given_LockHeld_When_OtherChannelAcquired_Then_DoesNotWait()
    {
        // Arrange
        var provider = new ChannelLockProvider();
        using var first = await provider.AcquireAsync(CreateChannelId(0x01), TestContext.Current.CancellationToken);

        // Act
        var second = provider.AcquireAsync(CreateChannelId(0x02), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(second.IsCompletedSuccessfully);
        (await second).Dispose();
    }

    [Fact]
    public async Task Given_WaiterCancelled_When_LockReleased_Then_NoLockIsLeft()
    {
        // Arrange
        var provider = new ChannelLockProvider();
        var channelId = CreateChannelId(0x03);
        var first = await provider.AcquireAsync(channelId, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        // Act
        var waiter = provider.AcquireAsync(channelId, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        first.Dispose();

        // Assert
        Assert.Equal(0, provider.ActiveLockCount);
        using var again = provider.Acquire(channelId);
    }

    [Fact]
    public async Task Given_ReleaserDisposedTwice_When_Acquired_Then_LockIsReleasedOnlyOnce()
    {
        // Arrange
        var provider = new ChannelLockProvider();
        var channelId = CreateChannelId(0x04);
        var first = await provider.AcquireAsync(channelId, TestContext.Current.CancellationToken);
        first.Dispose();
        var second = await provider.AcquireAsync(channelId, TestContext.Current.CancellationToken);

        // Act
        first.Dispose();
        var third = provider.AcquireAsync(channelId, TestContext.Current.CancellationToken);

        // Assert (a second dispose of the first releaser must not let a third holder in next to the second)
        Assert.False(third.IsCompleted);
        second.Dispose();
        (await third.WaitAsync(s_timeout, TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    public async Task Given_ManyConcurrentHolders_When_SameChannel_Then_NeverMoreThanOneInside()
    {
        // Arrange
        var provider = new ChannelLockProvider();
        var channelId = CreateChannelId(0x05);
        var inside = 0;
        var maxInside = 0;
        var counter = 0;

        // Act
        var workers = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            using var channelLock = i % 2 == 0
                                        ? await provider.AcquireAsync(channelId)
                                        : provider.Acquire(channelId);
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxInside, now);
            var read = counter;
            await Task.Yield();
            counter = read + 1;
            Interlocked.Decrement(ref inside);
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(workers).WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, maxInside);
        Assert.Equal(50, counter);
        Assert.Equal(0, provider.ActiveLockCount);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
                return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static ChannelId CreateChannelId(byte fill)
    {
        return new ChannelId(Enumerable.Repeat(fill, 32).ToArray());
    }
}