namespace NLightning.Application.Gossip.Graph;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The gossip rate limits of the graph ingress (BOLT 7 plan §3.8, G5-T2): per <c>channel_update</c> channel direction a
/// token bucket (one accepted update per interval, a burst of a few), and per node one accepted
/// <c>node_announcement</c> per interval. Only accepted messages spend a token; the ingress asks
/// <see cref="CanAcceptUpdate"/> before the signature check (cheap refusal) and spends with
/// <see cref="TryAcquireUpdate"/> right before applying.
/// </summary>
/// <remarks>
/// Wall clock, as LND does: a node that re-signs its policy every few seconds gets the burst, then one update per
/// interval. An interval of zero or less turns a limit off. Entries whose bucket is full again (or whose node interval
/// passed) are forgotten by <see cref="Prune"/>, so the memory is bounded by the recently active channels and nodes.
/// Thread-safe.
/// </remarks>
public sealed class GossipRateLimiter
{
    private readonly TimeSpan _updateInterval;
    private readonly int _updateBurst;
    private readonly TimeSpan _nodeInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<(ShortChannelId, byte), Bucket> _updates = new();
    private readonly Dictionary<CompactPubKey, DateTimeOffset> _nodes = new();

    public GossipRateLimiter(TimeSpan updateInterval, int updateBurst, TimeSpan nodeInterval,
                             TimeProvider? timeProvider = null)
    {
        _updateInterval = updateInterval;
        _updateBurst = Math.Max(1, updateBurst);
        _nodeInterval = nodeInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The number of tracked channel directions and nodes.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _updates.Count + _nodes.Count;
        }
    }

    /// <summary>True when an update of this channel direction would be accepted now (spends nothing).</summary>
    public bool CanAcceptUpdate(ShortChannelId shortChannelId, byte direction)
    {
        if (_updateInterval <= TimeSpan.Zero)
            return true;

        lock (_lock)
            return !_updates.TryGetValue((shortChannelId, direction), out var bucket)
                || Refill(bucket, _timeProvider.GetUtcNow()) >= 1;
    }

    /// <summary>Spends a token of this channel direction; false when none is left (rate limited).</summary>
    public bool TryAcquireUpdate(ShortChannelId shortChannelId, byte direction)
    {
        if (_updateInterval <= TimeSpan.Zero)
            return true;

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_updates.TryGetValue((shortChannelId, direction), out var bucket))
            {
                _updates[(shortChannelId, direction)] = new Bucket(_updateBurst - 1, now);
                return true;
            }

            var tokens = Refill(bucket, now);
            if (tokens < 1)
                return false;

            _updates[(shortChannelId, direction)] = new Bucket(tokens - 1, now);
            return true;
        }
    }

    /// <summary>True when an announcement of <paramref name="nodeId"/> would be accepted now (spends nothing).</summary>
    public bool CanAcceptNodeAnnouncement(CompactPubKey nodeId)
    {
        if (_nodeInterval <= TimeSpan.Zero)
            return true;

        lock (_lock)
            return !_nodes.TryGetValue(nodeId, out var last) || _timeProvider.GetUtcNow() - last >= _nodeInterval;
    }

    /// <summary>Records an accepted announcement of <paramref name="nodeId"/>; false when it came too soon.</summary>
    public bool TryAcquireNodeAnnouncement(CompactPubKey nodeId)
    {
        if (_nodeInterval <= TimeSpan.Zero)
            return true;

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeId, out var last) && now - last < _nodeInterval)
                return false;

            _nodes[nodeId] = now;
            return true;
        }
    }

    /// <summary>Forgets the entries that no longer limit anything; returns how many.</summary>
    public int Prune()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            var removed = 0;
            foreach (var key in _updates.Where(p => Refill(p.Value, now) >= _updateBurst).Select(p => p.Key).ToList())
                removed += _updates.Remove(key) ? 1 : 0;
            foreach (var key in _nodes.Where(p => now - p.Value >= _nodeInterval).Select(p => p.Key).ToList())
                removed += _nodes.Remove(key) ? 1 : 0;
            return removed;
        }
    }

    private double Refill(Bucket bucket, DateTimeOffset now)
    {
        var elapsed = now - bucket.At;
        if (elapsed <= TimeSpan.Zero)
            return bucket.Tokens;

        return Math.Min(_updateBurst, bucket.Tokens + elapsed / _updateInterval);
    }

    private readonly record struct Bucket(double Tokens, DateTimeOffset At);
}