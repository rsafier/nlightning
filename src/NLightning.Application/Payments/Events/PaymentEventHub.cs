using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NLightning.Application.Payments.Events;

using Domain.Payments.Events;
using Domain.Payments.Interfaces;

/// <summary>
/// The in-process payment event bus (Cashu plan C0, NL-812): <c>HtlcSwitch</c> publishes settled invoices and
/// <c>PaymentService</c> finished payments, each after its save; subscribers (the <c>waitinvoice</c>
/// command, the Cashu payment processor) read them through their own bounded queue.
/// </summary>
public sealed class PaymentEventHub : IPaymentEventPublisher, IPaymentEventSource
{
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();

    /// <summary>The number of live subscriptions.</summary>
    public int SubscriberCount => _subscriptions.Count;

    /// <inheritdoc />
    public void Publish(PaymentEvent paymentEvent)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        foreach (var subscription in _subscriptions.Keys)
            subscription.Write(paymentEvent);
    }

    /// <inheritdoc />
    public IPaymentEventSubscription Subscribe(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var subscription = new Subscription(this, capacity);
        _subscriptions[subscription] = 0;
        return subscription;
    }

    private void Remove(Subscription subscription) => _subscriptions.TryRemove(subscription, out _);

    private sealed class Subscription : IPaymentEventSubscription
    {
        private readonly PaymentEventHub _hub;
        private readonly Channel<PaymentEvent> _channel;
        private volatile bool _overflowed;

        public Subscription(PaymentEventHub hub, int capacity)
        {
            _hub = hub;
            _channel = Channel.CreateBounded<PaymentEvent>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = false,
                    SingleWriter = false
                }, _ => _overflowed = true);
        }

        public bool Overflowed => _overflowed;

        public void Write(PaymentEvent paymentEvent) => _channel.Writer.TryWrite(paymentEvent);

        public async IAsyncEnumerable<PaymentEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var paymentEvent in _channel.Reader.ReadAllAsync(cancellationToken))
                yield return paymentEvent;
        }

        public void Dispose()
        {
            _hub.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}