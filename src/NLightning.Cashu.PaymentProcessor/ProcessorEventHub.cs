using System.Threading.Channels;

namespace NLightning.Cashu.PaymentProcessor;

using Grpc;

/// <summary>
/// Fans the processor's events out to every open <c>WaitPaymentEvent</c> stream (NL-997): the node's payment events
/// mapped to the mint's quotes, on-chain deposits that reached their confirmations and on-chain melts that confirmed.
/// </summary>
/// <remarks>
/// Each stream has a bounded queue that drops its oldest event when full and is marked <see cref="Subscription.Overflowed"/>:
/// a mint that reads too slowly recovers through its checks, as after a reconnection. Events published while no stream
/// is open are dropped for the same reason.
/// </remarks>
internal sealed class ProcessorEventHub
{
    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>How many streams are open.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_lock)
                return _subscriptions.Count;
        }
    }

    /// <summary>Opens a stream's queue; dispose it to close.</summary>
    public Subscription Subscribe(int capacity = 1_024)
    {
        var subscription = new Subscription(this, capacity);
        lock (_lock)
            _subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>Queues <paramref name="response"/> for every open stream.</summary>
    public void Publish(PaymentEventResponse response)
    {
        Subscription[] subscriptions;
        lock (_lock)
            subscriptions = [.. _subscriptions];

        foreach (var subscription in subscriptions)
            subscription.Write(response);
    }

    /// <summary>
    /// Ends every open stream as overflowed: the processor itself missed node events (its own subscription
    /// overflowed), so each mint must subscribe again and check its quotes (NL-999).
    /// </summary>
    public void AbortAll()
    {
        Subscription[] subscriptions;
        lock (_lock)
            subscriptions = [.. _subscriptions];

        foreach (var subscription in subscriptions)
            subscription.Abort();
    }

    private void Remove(Subscription subscription)
    {
        lock (_lock)
            _subscriptions.Remove(subscription);
    }

    /// <summary>One stream's queue.</summary>
    internal sealed class Subscription : IDisposable
    {
        private readonly Channel<PaymentEventResponse> _channel;
        private readonly ProcessorEventHub _hub;
        private int _overflowed;

        public Subscription(ProcessorEventHub hub, int capacity)
        {
            _hub = hub;
            _channel = Channel.CreateBounded<PaymentEventResponse>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true
                }, _ => Interlocked.Exchange(ref _overflowed, 1));
        }

        /// <summary>Whether an event was dropped because the stream read too slowly.</summary>
        public bool Overflowed => Volatile.Read(ref _overflowed) == 1;

        public IAsyncEnumerable<PaymentEventResponse> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Write(PaymentEventResponse response) => _channel.Writer.TryWrite(response);

        /// <summary>Marks the stream overflowed and ends its reads with <see cref="EventsDroppedException"/>.</summary>
        public void Abort()
        {
            Interlocked.Exchange(ref _overflowed, 1);
            _channel.Writer.TryComplete(new EventsDroppedException());
        }

        public void Dispose()
        {
            _hub.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}

/// <summary>The processor dropped events a stream should have had (NL-999).</summary>
internal sealed class EventsDroppedException() : Exception("Payment events were dropped; subscribe again.");