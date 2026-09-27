namespace NLightning.Application.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// The token-bucket limits of <see cref="OnionMessageRateLimiter"/> (BOLT 4 onion messages, reader: MAY rate-limit by
/// dropping). A rate or a burst of 0 or less turns that bucket off (unlimited).
/// </summary>
/// <remarks>
/// <para>Defaults (BOLT12 plan §3.4, to be tuned): per peer 64 KiB/s with a 256 KiB burst and 20 messages/s with a
/// burst of 20 (the 21st message in one second from a peer is dropped); in total 640 KiB/s with a 2,560 KiB burst and
/// 200 messages/s with a burst of 200. For comparison, LND 0.21 allows 512 kbit/s with a 256 KiB burst per peer and
/// 5,120 kbit/s with a 1,600 KiB burst in total (unverified, from its release notes).</para>
/// <para>A message longer than a byte burst is never admitted, so keep the byte bursts above the largest onion message
/// (the 32,834-byte packet a writer uses for large payloads; the default 256 KiB holds seven).</para>
/// </remarks>
/// <param name="PeerBytesPerSecond">Refill of each peer's byte bucket.</param>
/// <param name="PeerBurstBytes">Capacity of each peer's byte bucket.</param>
/// <param name="PeerMessagesPerSecond">Refill of each peer's message bucket.</param>
/// <param name="PeerBurstMessages">Capacity of each peer's message bucket.</param>
/// <param name="GlobalBytesPerSecond">Refill of the node-wide byte bucket.</param>
/// <param name="GlobalBurstBytes">Capacity of the node-wide byte bucket.</param>
/// <param name="GlobalMessagesPerSecond">Refill of the node-wide message bucket.</param>
/// <param name="GlobalBurstMessages">Capacity of the node-wide message bucket.</param>
public sealed record OnionMessageRateLimits(
    double PeerBytesPerSecond = OnionMessageRateLimits.DefaultPeerBytesPerSecond,
    double PeerBurstBytes = OnionMessageRateLimits.DefaultPeerBurstBytes,
    double PeerMessagesPerSecond = OnionMessageRateLimits.DefaultPeerMessagesPerSecond,
    double PeerBurstMessages = OnionMessageRateLimits.DefaultPeerBurstMessages,
    double GlobalBytesPerSecond = OnionMessageRateLimits.DefaultGlobalBytesPerSecond,
    double GlobalBurstBytes = OnionMessageRateLimits.DefaultGlobalBurstBytes,
    double GlobalMessagesPerSecond = OnionMessageRateLimits.DefaultGlobalMessagesPerSecond,
    double GlobalBurstMessages = OnionMessageRateLimits.DefaultGlobalBurstMessages)
{
    /// <summary>64 KiB/s.</summary>
    public const double DefaultPeerBytesPerSecond = 64 * 1024;

    /// <summary>256 KiB.</summary>
    public const double DefaultPeerBurstBytes = 256 * 1024;

    /// <summary>20 messages/s.</summary>
    public const double DefaultPeerMessagesPerSecond = 20;

    /// <summary>20 messages.</summary>
    public const double DefaultPeerBurstMessages = 20;

    /// <summary>640 KiB/s.</summary>
    public const double DefaultGlobalBytesPerSecond = 640 * 1024;

    /// <summary>2,560 KiB.</summary>
    public const double DefaultGlobalBurstBytes = 2560 * 1024;

    /// <summary>200 messages/s.</summary>
    public const double DefaultGlobalMessagesPerSecond = 200;

    /// <summary>200 messages.</summary>
    public const double DefaultGlobalBurstMessages = 200;

    /// <summary>The defaults above.</summary>
    public static OnionMessageRateLimits Default { get; } = new();
}

/// <summary>
/// <see cref="IOnionMessageRateLimiter"/> with token buckets (BOLT 4 onion messages: a reader MAY rate-limit by
/// dropping, and nothing is sent back): per peer and node-wide, each in bytes and in messages. A message is admitted
/// only when all four buckets hold its tokens, and then takes them from all four; a dropped message takes nothing.
/// </summary>
/// <remarks>
/// Buckets start full and refill continuously with the <see cref="TimeProvider"/>'s monotonic timestamp. A peer that
/// disconnects (<see cref="RemovePeer"/>) is forgotten at once when its buckets are full again, else only once they
/// have refilled (checked every <see cref="SweepInterval"/> admissions), so reconnecting never refills a peer's
/// buckets early. Thread-safe (one lock; every call is a few arithmetic operations).
/// </remarks>
public sealed class OnionMessageRateLimiter : IOnionMessageRateLimiter
{
    /// <summary>How many <see cref="TryAdmit"/> calls pass between two sweeps of disconnected peers.</summary>
    internal const int SweepInterval = 256;

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly OnionMessageRateLimits _limits;
    private readonly Dictionary<CompactPubKey, PeerBuckets> _peers = new();
    private readonly TokenBucket _globalBytes;
    private readonly TokenBucket _globalMessages;
    private int _callsSinceSweep;
    private long _admitted;
    private long _dropped;

    /// <param name="limits">The limits; null takes <see cref="OnionMessageRateLimits.Default"/>.</param>
    /// <param name="timeProvider">The clock; null takes <see cref="TimeProvider.System"/>.</param>
    public OnionMessageRateLimiter(OnionMessageRateLimits? limits = null, TimeProvider? timeProvider = null)
    {
        _limits = limits ?? OnionMessageRateLimits.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var now = _timeProvider.GetTimestamp();
        _globalBytes = new TokenBucket(_limits.GlobalBytesPerSecond, _limits.GlobalBurstBytes, now);
        _globalMessages = new TokenBucket(_limits.GlobalMessagesPerSecond, _limits.GlobalBurstMessages, now);
    }

    /// <summary>The limits in use.</summary>
    public OnionMessageRateLimits Limits => _limits;

    /// <summary>The messages admitted so far.</summary>
    public long AdmittedCount => Interlocked.Read(ref _admitted);

    /// <summary>The messages dropped so far (over a limit, or with a negative length).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>The peers with a bucket (connected, or disconnected and not refilled yet).</summary>
    internal int TrackedPeerCount
    {
        get
        {
            lock (_lock)
                return _peers.Count;
        }
    }

    /// <inheritdoc />
    public bool TryAdmit(CompactPubKey peerNodeId, int messageLength)
    {
        if (messageLength < 0)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        bool admitted;
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            if (!_peers.TryGetValue(peerNodeId, out var peer))
            {
                peer = new PeerBuckets(_limits, now);
                _peers[peerNodeId] = peer;
            }

            peer.Disconnected = false;
            peer.Refill(now, _timeProvider);
            _globalBytes.Refill(now, _timeProvider);
            _globalMessages.Refill(now, _timeProvider);

            admitted = peer.Bytes.Holds(messageLength) && peer.Messages.Holds(1)
                    && _globalBytes.Holds(messageLength) && _globalMessages.Holds(1);
            if (admitted)
            {
                peer.Bytes.Take(messageLength);
                peer.Messages.Take(1);
                _globalBytes.Take(messageLength);
                _globalMessages.Take(1);
            }

            if (++_callsSinceSweep >= SweepInterval)
            {
                _callsSinceSweep = 0;
                SweepDisconnected(now);
            }
        }

        Interlocked.Increment(ref admitted ? ref _admitted : ref _dropped);
        return admitted;
    }

    /// <inheritdoc />
    /// <remarks>A peer whose buckets are not full yet is kept (marked disconnected) until they are.</remarks>
    public void RemovePeer(CompactPubKey peerNodeId)
    {
        lock (_lock)
        {
            if (!_peers.TryGetValue(peerNodeId, out var peer))
                return;

            peer.Refill(_timeProvider.GetTimestamp(), _timeProvider);
            if (peer.IsFull)
                _peers.Remove(peerNodeId);
            else
                peer.Disconnected = true;
        }
    }

    private void SweepDisconnected(long now)
    {
        List<CompactPubKey>? refilled = null;
        foreach (var (nodeId, peer) in _peers)
        {
            if (!peer.Disconnected)
                continue;

            peer.Refill(now, _timeProvider);
            if (peer.IsFull)
                (refilled ??= []).Add(nodeId);
        }

        if (refilled is null)
            return;

        foreach (var nodeId in refilled)
            _peers.Remove(nodeId);
    }

    private sealed class PeerBuckets(OnionMessageRateLimits limits, long now)
    {
        public TokenBucket Bytes { get; } = new(limits.PeerBytesPerSecond, limits.PeerBurstBytes, now);
        public TokenBucket Messages { get; } = new(limits.PeerMessagesPerSecond, limits.PeerBurstMessages, now);
        public bool Disconnected { get; set; }
        public bool IsFull => Bytes.IsFull && Messages.IsFull;

        public void Refill(long now, TimeProvider timeProvider)
        {
            Bytes.Refill(now, timeProvider);
            Messages.Refill(now, timeProvider);
        }
    }

    /// <summary>
    /// A token bucket: <c>capacity</c> tokens at most, <c>rate</c> tokens per second; off (always holds) when either is
    /// 0 or less. Not thread-safe (the limiter's lock guards it).
    /// </summary>
    private sealed class TokenBucket
    {
        private readonly double _rate;
        private readonly double _capacity;
        private readonly bool _unlimited;
        private double _tokens;
        private long _lastRefill;

        public TokenBucket(double rate, double capacity, long now)
        {
            _unlimited = !(rate > 0) || !(capacity > 0);
            _rate = rate;
            _capacity = capacity;
            _tokens = capacity;
            _lastRefill = now;
        }

        public bool IsFull => _unlimited || _tokens >= _capacity;

        public void Refill(long now, TimeProvider timeProvider)
        {
            if (_unlimited)
                return;

            if (now > _lastRefill)
                _tokens = Math.Min(_capacity,
                                   _tokens + timeProvider.GetElapsedTime(_lastRefill, now).TotalSeconds * _rate);

            _lastRefill = now;
        }

        public bool Holds(double tokens) => _unlimited || _tokens >= tokens;

        public void Take(double tokens)
        {
            if (!_unlimited)
                _tokens -= tokens;
        }
    }
}