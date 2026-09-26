namespace NLightning.Application.Gossip.Graph;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Gossip that arrived before what it depends on (plan BOLT7 §3.3 stage 5): a <c>channel_update</c> whose
/// <c>channel_announcement</c> is not in the graph yet, and a <c>node_announcement</c> whose node has no channel yet.
/// Peers send a channel's announcement before its updates, but the ingress checks announcements on parallel workers
/// (the chain lookup takes a while), so an update often overtakes its announcement. The ingress replays what is kept
/// here once the channel is added.
/// </summary>
/// <remarks>
/// Only the newest message per channel direction (per node) is kept; entries expire after the TTL, and the cache
/// holds at most its capacity (a new entry is refused when it is full of live ones). Nothing here was verified: a
/// replayed message goes through the whole pipeline. Thread-safe.
/// </remarks>
public sealed class OrphanUpdateCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<(ShortChannelId, byte), OrphanEntry<ChannelUpdateMessage>> _updates = new();
    private readonly Dictionary<CompactPubKey, OrphanEntry<NodeAnnouncementMessage>> _nodes = new();

    public OrphanUpdateCache(int capacity, TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _ttl = ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The number of kept messages (expired ones included until the next prune).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _updates.Count + _nodes.Count;
        }
    }

    /// <summary>
    /// Keeps <paramref name="message"/> until its channel arrives, replacing an older one of the same direction.
    /// </summary>
    /// <returns>False when it was not kept (full, or an equal or newer one is kept).</returns>
    public bool AddUpdate(ChannelUpdateMessage message, IPeerService? origin) => AddUpdate(message, origin, out _);

    /// <summary>
    /// Keeps <paramref name="message"/> until its channel arrives; <paramref name="full"/> tells a refusal for lack of
    /// room (a metric) from one for a kept equal or newer message.
    /// </summary>
    public bool AddUpdate(ChannelUpdateMessage message, IPeerService? origin, out bool full)
    {
        ArgumentNullException.ThrowIfNull(message);
        full = false;
        var key = (message.Payload.ShortChannelId, message.Payload.Direction ? (byte)1 : (byte)0);
        lock (_lock)
        {
            if (_updates.TryGetValue(key, out var existing))
            {
                if (existing.Message.Payload.Timestamp >= message.Payload.Timestamp && !IsExpired(existing))
                    return false;

                _updates[key] = new OrphanEntry<ChannelUpdateMessage>(message, origin, _timeProvider.GetUtcNow());
                return true;
            }

            if (!HasRoom())
            {
                full = true;
                return false;
            }

            _updates[key] = new OrphanEntry<ChannelUpdateMessage>(message, origin, _timeProvider.GetUtcNow());
            return true;
        }
    }

    /// <summary>Keeps <paramref name="message"/> until its node has a channel, replacing an older one.</summary>
    /// <returns>False when it was not kept (full, or an equal or newer one is kept).</returns>
    public bool AddNodeAnnouncement(NodeAnnouncementMessage message, IPeerService? origin) =>
        AddNodeAnnouncement(message, origin, out _);

    /// <summary>
    /// Keeps <paramref name="message"/> until its node has a channel; <paramref name="full"/> tells a refusal for lack
    /// of room from one for a kept equal or newer message.
    /// </summary>
    public bool AddNodeAnnouncement(NodeAnnouncementMessage message, IPeerService? origin, out bool full)
    {
        ArgumentNullException.ThrowIfNull(message);
        full = false;
        var key = message.Payload.NodeId;
        lock (_lock)
        {
            if (_nodes.TryGetValue(key, out var existing))
            {
                if (existing.Message.Payload.Timestamp >= message.Payload.Timestamp && !IsExpired(existing))
                    return false;

                _nodes[key] = new OrphanEntry<NodeAnnouncementMessage>(message, origin, _timeProvider.GetUtcNow());
                return true;
            }

            if (!HasRoom())
            {
                full = true;
                return false;
            }

            _nodes[key] = new OrphanEntry<NodeAnnouncementMessage>(message, origin, _timeProvider.GetUtcNow());
            return true;
        }
    }

    /// <summary>Removes and returns the live updates kept for <paramref name="shortChannelId"/>.</summary>
    public IReadOnlyList<OrphanEntry<ChannelUpdateMessage>> TakeUpdates(ShortChannelId shortChannelId)
    {
        var taken = new List<OrphanEntry<ChannelUpdateMessage>>(2);
        lock (_lock)
        {
            for (byte direction = 0; direction <= 1; direction++)
            {
                if (_updates.Remove((shortChannelId, direction), out var entry) && !IsExpired(entry))
                    taken.Add(entry);
            }
        }

        return taken;
    }

    /// <summary>Removes and returns the live announcement kept for <paramref name="nodeId"/>, if any.</summary>
    public OrphanEntry<NodeAnnouncementMessage>? TakeNodeAnnouncement(CompactPubKey nodeId)
    {
        lock (_lock)
            return _nodes.Remove(nodeId, out var entry) && !IsExpired(entry) ? entry : null;
    }

    /// <summary>Drops the expired entries; returns how many.</summary>
    public int PruneExpired()
    {
        lock (_lock)
            return PruneExpiredLocked();
    }

    private bool HasRoom()
    {
        if (_updates.Count + _nodes.Count < _capacity)
            return true;

        PruneExpiredLocked();
        return _updates.Count + _nodes.Count < _capacity;
    }

    private int PruneExpiredLocked()
    {
        var removed = 0;
        foreach (var key in _updates.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList())
            removed += _updates.Remove(key) ? 1 : 0;
        foreach (var key in _nodes.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList())
            removed += _nodes.Remove(key) ? 1 : 0;
        return removed;
    }

    private bool IsExpired<T>(OrphanEntry<T> entry) => _timeProvider.GetUtcNow() - entry.AddedAt > _ttl;
}

/// <summary>A kept orphan message, the peer it came from (null for our own) and when it was kept.</summary>
public sealed record OrphanEntry<T>(T Message, IPeerService? Origin, DateTimeOffset AddedAt);