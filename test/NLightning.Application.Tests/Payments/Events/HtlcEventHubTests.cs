namespace NLightning.Application.Tests.Payments.Events;

using Application.Payments.Events;
using Domain.Payments.Events;

public class HtlcEventHubTests
{
    private static HtlcActivityEvent Activity(ulong id) => new(HtlcActivityKind.Forward, HtlcActivityRole.Send,
        0, 0, 123, id, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Given_TwoReaders_When_OneOverflows_Then_OnlySlowReaderEndsAndProducerNeverWaits()
    {
        var hub = new HtlcEventHub();
        using var slow = hub.Subscribe(1);
        using var fast = hub.Subscribe(4);
        hub.Publish(Activity(1));
        hub.Publish(Activity(2));
        Assert.True(slow.Overflowed);
        Assert.True(slow.OverflowCancellationToken.IsCancellationRequested);
        Assert.False(fast.Overflowed);
        Assert.Equal(1, hub.SubscriberCount);
        await using var reader = fast.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1UL, reader.Current.OutgoingHtlcId);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(2UL, reader.Current.OutgoingHtlcId);
    }

    [Fact]
    public async Task Given_AsynchronousObservation_When_ANewReaderSubscribes_Then_PreSubscriptionActivityIsNotReplayed()
    {
        var hub = new HtlcEventHub();
        using var existing = hub.Subscribe();
        var captured = hub.CapturePublisher();
        using var later = hub.Subscribe();
        captured(Activity(1));
        await using var first = existing.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await first.MoveNextAsync());
        later.Dispose();
        await using var second = later.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await second.MoveNextAsync());
    }

    [Fact]
    public void Given_ThrowingCancellationCallback_When_Overflowing_Then_ProducerStillReturns()
    {
        var hub = new HtlcEventHub();
        using var reader = hub.Subscribe(1);
        using var registration = reader.OverflowCancellationToken.Register(() => throw new InvalidOperationException("subscriber failure"));
        hub.Publish(Activity(1));
        hub.Publish(Activity(2));
        Assert.True(reader.Overflowed);
        Assert.False(hub.HasSubscribers);
    }

    [Fact]
    public async Task Given_BlockingCancellationCallback_When_Overflowing_Then_ProducerCompletesIndependently()
    {
        var hub = new HtlcEventHub();
        using var reader = hub.Subscribe(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var registration = reader.OverflowCancellationToken.Register(() =>
        {
            entered.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        hub.Publish(Activity(1));
        var publish = Task.Run(() => hub.Publish(Activity(2)), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await publish.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(reader.Overflowed);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Given_DisposedReader_When_Publishing_Then_ReaderEndsAndSubscriptionIsRemoved()
    {
        var hub = new HtlcEventHub();
        var subscription = hub.Subscribe();
        subscription.Dispose();
        hub.Publish(Activity(1));
        Assert.False(hub.HasSubscribers);
        await using var reader = subscription.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await reader.MoveNextAsync());
    }
}