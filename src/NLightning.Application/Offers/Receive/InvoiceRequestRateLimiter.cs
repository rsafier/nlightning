namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.ValueObjects;

/// <summary>
/// Token buckets for the invoice_requests we answer (BOLT 12 plan §3.4, D11): one per offer and one for the node, each
/// with a burst of one second's rate. Every request takes a node-wide token first (<see cref="TryTakeGlobal"/>, before
/// it is parsed or its signature checked); one for an offer of ours then also takes one from that offer's bucket.
/// </summary>
/// <remarks>
/// Every answered request signs an invoice and stores a row, so a flood is dropped here rather than answered. Buckets
/// start full; an offer's bucket is forgotten once full again (swept every <see cref="SweepInterval"/> admissions).
/// Thread-safe.
/// </remarks>
public sealed class InvoiceRequestRateLimiter
{
    /// <summary>How many calls between two sweeps of full per-offer buckets.</summary>
    public const int SweepInterval = 256;

    private readonly object _lock = new();
    private readonly Dictionary<Hash, Bucket> _offers = new();
    private readonly TimeProvider _timeProvider;
    private readonly double _perOfferRate;
    private readonly Bucket _global;
    private int _calls;

    public InvoiceRequestRateLimiter(double perOfferPerSecond, double globalPerSecond, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perOfferPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalPerSecond);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _perOfferRate = perOfferPerSecond;
        _global = new Bucket(globalPerSecond, _timeProvider.GetTimestamp());
    }

    /// <summary>How many offers have a bucket (for tests).</summary>
    public int TrackedOffers
    {
        get
        {
            lock (_lock)
                return _offers.Count;
        }
    }

    /// <summary>
    /// Takes one token from the node-wide bucket: every invoice_request we look at costs one, before any parsing, so
    /// requests with bad signatures, unknown offers or the wrong path count against the node-wide rate too.
    /// </summary>
    /// <returns>False (nothing taken) when it is empty.</returns>
    public bool TryTakeGlobal()
    {
        lock (_lock)
        {
            _global.Refill(_timeProvider.GetTimestamp(), _timeProvider.TimestampFrequency);
            if (_global.Tokens < 1)
                return false;

            _global.Tokens--;
            return true;
        }
    }

    /// <summary>
    /// Takes one token from the offer's bucket (the node-wide one was taken by <see cref="TryTakeGlobal"/>).
    /// </summary>
    /// <returns>False (nothing taken) when it is empty.</returns>
    public bool TryAdmit(Hash offerId)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            var frequency = _timeProvider.TimestampFrequency;
            if (++_calls % SweepInterval == 0)
                Sweep(now, frequency);

            if (!_offers.TryGetValue(offerId, out var offer))
            {
                offer = new Bucket(_perOfferRate, now);
                _offers[offerId] = offer;
            }

            offer.Refill(now, frequency);
            if (offer.Tokens < 1)
                return false;

            offer.Tokens--;
            return true;
        }
    }

    private void Sweep(long now, long frequency)
    {
        foreach (var (id, bucket) in _offers.ToList())
        {
            bucket.Refill(now, frequency);
            if (bucket.Tokens >= bucket.Capacity)
                _offers.Remove(id);
        }
    }

    private sealed class Bucket
    {
        private readonly double _rate;
        private long _last;

        public Bucket(double rate, long now)
        {
            _rate = rate;
            Capacity = Math.Max(1, rate);
            Tokens = Capacity;
            _last = now;
        }

        public double Capacity { get; }

        public double Tokens { get; set; }

        public void Refill(long now, long frequency)
        {
            if (now <= _last)
                return;

            Tokens = Math.Min(Capacity, Tokens + (now - _last) * _rate / frequency);
            _last = now;
        }
    }
}