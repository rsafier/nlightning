namespace NLightning.Application.Gossip.Graph;

using Domain.Crypto.ValueObjects;

/// <summary>
/// The per-peer misbehaviour score of the graph ingress (BOLT 7 plan §3.3, §3.8, G5-T2): each invalid signature, bad
/// encoding or funding output that contradicts its announcement counts one; <see cref="Record"/> reports when a peer
/// reached the threshold inside the sliding window, and then starts it over. Keyed by the peer's node id, so the score
/// survives reconnections. Memory is bounded: at most the threshold entries per peer, and peers without a recent
/// entry are forgotten by <see cref="Prune"/>. Thread-safe.
/// </summary>
public sealed class GossipMisbehaviourTracker
{
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<CompactPubKey, Queue<DateTimeOffset>> _events = new();

    public GossipMisbehaviourTracker(int threshold, TimeSpan window, TimeProvider? timeProvider = null)
    {
        _threshold = threshold;
        _window = window;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The number of peers with a recent entry.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _events.Count;
        }
    }

    /// <summary>The peer's entries inside the window.</summary>
    public int GetScore(CompactPubKey peer)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_events.TryGetValue(peer, out var events))
                return 0;

            Expire(events, now);
            return events.Count;
        }
    }

    /// <summary>
    /// Counts one misbehaviour of <paramref name="peer"/>. True when that reached the threshold inside the window
    /// (the peer is to be banned); its score then starts over. A threshold of zero or less never reports.
    /// </summary>
    public bool Record(CompactPubKey peer)
    {
        if (_threshold <= 0)
            return false;

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_events.TryGetValue(peer, out var events))
                _events[peer] = events = new Queue<DateTimeOffset>(_threshold);

            Expire(events, now);
            events.Enqueue(now);
            if (events.Count < _threshold)
                return false;

            _events.Remove(peer);
            return true;
        }
    }

    /// <summary>Forgets the peers without an entry inside the window; returns how many.</summary>
    public int Prune()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            var removed = 0;
            foreach (var (peer, events) in _events.ToList())
            {
                Expire(events, now);
                if (events.Count == 0 && _events.Remove(peer))
                    removed++;
            }

            return removed;
        }
    }

    private void Expire(Queue<DateTimeOffset> events, DateTimeOffset now)
    {
        while (events.Count > 0 && now - events.Peek() >= _window)
            events.Dequeue();
    }
}