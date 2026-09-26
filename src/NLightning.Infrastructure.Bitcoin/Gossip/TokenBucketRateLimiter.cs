namespace NLightning.Infrastructure.Bitcoin.Gossip;

/// <summary>
/// A token bucket over a <see cref="TimeProvider"/>: <c>ratePerSecond</c> tokens per second, at most
/// <c>ratePerSecond</c> stored (the burst). A caller that finds the bucket empty reserves the next token (the balance
/// goes negative) and waits until it is due, so waiters are served in arrival order at the configured rate.
/// </summary>
internal sealed class TokenBucketRateLimiter
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private double _tokens;
    private long _lastRefill;

    public TokenBucketRateLimiter(int ratePerSecond, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ratePerSecond, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _ratePerSecond = ratePerSecond;
        _capacity = ratePerSecond;
        _tokens = _capacity;
        _lastRefill = timeProvider.GetTimestamp();
    }

    /// <summary>Takes one token, waiting until one is due. A cancelled wait does not give the token back.</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        var delay = Reserve();
        return delay <= TimeSpan.Zero
                   ? Task.CompletedTask
                   : Task.Delay(delay, _timeProvider, cancellationToken);
    }

    /// <summary>Takes one token and returns how long the caller must wait before using it.</summary>
    internal TimeSpan Reserve()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            var elapsed = _timeProvider.GetElapsedTime(_lastRefill, now).TotalSeconds;
            _lastRefill = now;
            _tokens = Math.Min(_capacity, _tokens + elapsed * _ratePerSecond);

            _tokens -= 1;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _ratePerSecond);
        }
    }
}