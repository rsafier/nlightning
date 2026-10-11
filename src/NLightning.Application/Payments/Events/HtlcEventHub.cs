using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NLightning.Application.Payments.Events;

using Domain.Payments.Events;
using Domain.Payments.Interfaces;

/// <summary>Nonblocking fanout. Overflow closes only the slow reader; no silent event loss or producer waits.</summary>
public sealed class HtlcEventHub : IHtlcEventPublisher, IHtlcEventSource
{
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();
    public int SubscriberCount => _subscriptions.Count;
    public bool HasSubscribers => !_subscriptions.IsEmpty;

    public void Publish(HtlcActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        foreach (var subscription in _subscriptions.Keys)
            subscription.Write(activity);
    }

    public Action<HtlcActivityEvent> CapturePublisher()
    {
        var recipients = _subscriptions.Keys.ToArray();
        return activity =>
        {
            foreach (var subscription in recipients)
                subscription.Write(activity);
        };
    }

    public void InvalidateSubscriptions()
    {
        foreach (var subscription in _subscriptions.Keys)
            subscription.Invalidate();
    }

    public IHtlcEventSubscription Subscribe(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var subscription = new Subscription(this, capacity);
        _subscriptions[subscription] = 0;
        return subscription;
    }

    private sealed class Subscription : IHtlcEventSubscription
    {
        private readonly HtlcEventHub _hub;
        private readonly Channel<HtlcActivityEvent> _channel;
        private int _overflowed;
        private readonly CancellationTokenSource _overflow = new();
        public CancellationToken OverflowCancellationToken => _overflow.Token;
        public bool Overflowed => Volatile.Read(ref _overflowed) != 0;

        public Subscription(HtlcEventHub hub, int capacity)
        {
            _hub = hub;
            _channel = Channel.CreateBounded<HtlcActivityEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public void Write(HtlcActivityEvent activity)
        {
            if (_channel.Writer.TryWrite(activity))
                return;
            Invalidate();
        }

        public void Invalidate()
        {
            if (Interlocked.Exchange(ref _overflowed, 1) == 0)
            {
                Dispose();
                _ = CancelOverflowAsync();
            }
        }

        private async Task CancelOverflowAsync()
        {
            try
            {
                await _overflow.CancelAsync();
            }
            catch (AggregateException)
            {
                // Cancellation callbacks belong to readers; they cannot block or interrupt the producer.
            }
        }

        public async IAsyncEnumerable<HtlcActivityEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var activity in _channel.Reader.ReadAllAsync(cancellationToken))
                yield return activity;
        }

        public void Dispose()
        {
            _hub._subscriptions.TryRemove(this, out _);
            _channel.Writer.TryComplete();
        }
    }
}