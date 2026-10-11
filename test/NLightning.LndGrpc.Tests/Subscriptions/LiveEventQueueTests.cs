using Grpc.Core;

namespace NLightning.LndGrpc.Tests.Subscriptions;

using LndGrpc.Services;

public sealed class LiveEventQueueTests
{
    [Fact]
    public async Task Given_ASlowConsumer_When_ItsQueueOverflows_Then_TheBlockedWriteEndsWithResourceExhausted()
    {
        using var queue = new LiveEventQueue<int>(1);
        var writer = new BlockingWriter();
        queue.Publish(1);
        var stream = queue.WriteToAsync(writer, TestContext.Current.CancellationToken);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        queue.Publish(2);
        queue.Publish(3);
        var error = await Assert.ThrowsAsync<RpcException>(() => stream);
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    [Fact]
    public async Task Given_ATerminatedConsumer_When_EventsArePublished_Then_ThePublisherDoesNotWait()
    {
        using var queue = new LiveEventQueue<int>(1);
        queue.Dispose();
        queue.Publish(1);
        var error = await Assert.ThrowsAsync<RpcException>(() => queue.WriteToAsync(new BlockingWriter(),
            TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    [Fact]
    public async Task Given_ALiveStream_When_TheCallerCancels_Then_TheWaitEnds()
    {
        using var queue = new LiveEventQueue<int>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = queue.WriteToAsync(new BlockingWriter(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        Assert.False(queue.Overflowed);
    }

    private sealed class BlockingWriter : IServerStreamWriter<int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(int message) => throw new NotSupportedException();
        public async Task WriteAsync(int message, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}