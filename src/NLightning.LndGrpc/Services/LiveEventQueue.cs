using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

/// <summary>A bounded live subscription. Publishers never wait for a slow RPC consumer.</summary>
internal sealed class LiveEventQueue<T> : IDisposable
{
    private readonly Channel<T> _queue;
    private readonly CancellationTokenSource _overflow = new();
    private RpcException? _failure;

    public LiveEventQueue(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _queue = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool Overflowed => Volatile.Read(ref _failure)?.StatusCode == StatusCode.ResourceExhausted;

    public void Publish(T value)
    {
        if (Volatile.Read(ref _failure) is not null || _queue.Writer.TryWrite(value))
            return;
        Fail(OverflowError());
    }

    public void Fail(RpcException error)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) is null)
        {
            _queue.Writer.TryComplete();
            _ = CancelSafelyAsync();
        }
    }

    private async Task CancelSafelyAsync()
    {
        try
        {
            await _overflow.CancelAsync();
        }
        catch (AggregateException)
        {
            // A consumer's cancellation callback must not escape into a node event publisher.
        }
    }

    public Task WriteToAsync(IServerStreamWriter<T> stream, CancellationToken cancellationToken) =>
        WriteToAsync(stream, static value => ValueTask.FromResult(value), cancellationToken);

    public async Task WriteToAsync<TResult>(IServerStreamWriter<TResult> stream,
                                            Func<T, ValueTask<TResult>> map, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _overflow.Token);
        try
        {
            await foreach (var value in _queue.Reader.ReadAllAsync(linked.Token))
            {
                if (_failure is not null)
                    throw _failure;
                await stream.WriteAsync(await map(value), linked.Token);
            }
        }
        catch (OperationCanceledException) when (_failure is not null && !cancellationToken.IsCancellationRequested)
        {
            throw _failure;
        }
        if (_failure is not null && !cancellationToken.IsCancellationRequested)
            throw _failure;
    }

    public async IAsyncEnumerable<T> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _overflow.Token);
        while (true)
        {
            bool available;
            try
            {
                available = await _queue.Reader.WaitToReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (_failure is not null && !cancellationToken.IsCancellationRequested)
            {
                throw _failure;
            }
            if (_failure is not null)
                throw _failure;
            if (!available)
                yield break;
            while (_queue.Reader.TryRead(out var value))
            {
                if (_failure is not null)
                    throw _failure;
                yield return value;
            }
        }
    }

    private static RpcException OverflowError() => new(new Status(StatusCode.ResourceExhausted,
        "live event subscription overflowed; reconnect and reconcile current state"));

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        // An already captured publisher can still run during unsubscribe. Keep the CTS usable for that callback.
    }
}