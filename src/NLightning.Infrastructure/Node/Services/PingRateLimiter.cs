namespace NLightning.Infrastructure.Node.Services;

/// <summary>
/// Decides which inbound <c>ping</c> messages of one peer we answer, against a peer flooding us with them: at most
/// <see cref="DefaultMaxAnsweredPings"/> pings per <see cref="DefaultInterval"/> are answered, further ones are
/// ignored (no <c>pong</c>, the connection stays up, NL-005). BOLT 1 mandates answering every ping whose
/// <c>num_pong_bytes</c> is below 65532, but only recommends "limited precautions" against ping flooding; peers
/// ping about once a minute, so a few answers per interval leave every legitimate use untouched while capping the
/// <c>pong</c> amplification (each answer can be 65 kB) a flooding peer gets out of us.
/// </summary>
internal sealed class PingRateLimiter
{
    /// <summary>
    /// How many pings of one peer are answered per interval before further ones are ignored.
    /// </summary>
    internal const int DefaultMaxAnsweredPings = 5;

    /// <summary>
    /// The window <see cref="DefaultMaxAnsweredPings"/> counts over.
    /// </summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly int _maxAnsweredPings;
    private readonly TimeSpan _interval;
    private readonly Queue<DateTimeOffset> _answeredPings = new();
    private readonly Lock _lock = new();

    public PingRateLimiter() : this(DefaultMaxAnsweredPings, DefaultInterval) { }

    internal PingRateLimiter(int maxAnsweredPings, TimeSpan interval)
    {
        _maxAnsweredPings = maxAnsweredPings;
        _interval = interval;
    }

    /// <summary>
    /// Register a ping to answer. False when the peer is over the limit: the ping must be ignored without a pong.
    /// </summary>
    public bool TryRegisterAnswer(DateTimeOffset now)
    {
        lock (_lock)
        {
            while (_answeredPings.Count > 0 && now - _answeredPings.Peek() >= _interval)
                _answeredPings.Dequeue();

            if (_answeredPings.Count >= _maxAnsweredPings)
                return false;

            _answeredPings.Enqueue(now);
            return true;
        }
    }
}