namespace NLightning.Application.Gossip.Graph;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Gossip that arrived before what it depends on (plan BOLT7 §3.3 stage 5): a <c>channel_update</c> whose
/// <c>channel_announcement</c> is not in the graph yet, and a <c>node_announcement</c> whose node has no channel yet.
/// Peers send a channel's announcement before its updates, and the ingress handles a channel's messages on one worker
/// in arrival order (NL-408), so an update waits here mostly when its announcement came from another peer later, or
/// when it does not match the pending announcement (NL-406). The ingress replays what is kept here once the
/// announcement arrives or the channel is added.
/// </summary>
/// <remarks>
/// Only the newest message per channel direction (per node) is kept; entries expire after the TTL, and the cache
/// holds at most its capacity (a new entry is refused when it is full of live ones). Nothing here was verified: a
/// replayed message goes through the whole pipeline. A <c>node_announcement</c> whose node's only channels are pending
/// (NL-406) is re-dated by the ingress (<see cref="RefreshNodeAnnouncement"/>) while a pending candidate names the
/// node (NL-425), so it waits for the promotion instead of expiring. Thread-safe.
/// </remarks>
public sealed class OrphanUpdateCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<(ShortChannelId, byte), OrphanEntry<ChannelUpdateMessage>> _updates = new();
    private readonly Dictionary<CompactPubKey, OrphanEntry<NodeAnnouncementMessage>> _nodes = new();
    private readonly Dictionary<(ShortChannelId, byte), OrphanEntry<ChannelUpdate2Message>> _updates2 = new();
    private readonly Dictionary<CompactPubKey, OrphanEntry<NodeAnnouncement2Message>> _nodes2 = new();

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
                return TotalLocked;
        }
    }

    private int TotalLocked => _updates.Count + _nodes.Count + _updates2.Count + _nodes2.Count;

    /// <summary>
    /// Keeps a <c>channel_update_2</c> (NL-878) until its channel arrives, replacing an older one (a lower block
    /// height) of the same direction; <paramref name="full"/> tells a refusal for lack of room.
    /// </summary>
    public bool AddUpdate2(ChannelUpdate2Message message, IPeerService? origin, out bool full)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Add(_updates2, (message.Payload.ShortChannelId, message.Payload.Direction), message, origin,
                   m => m.Payload.BlockHeight, out full);
    }

    /// <summary>
    /// Keeps a <c>node_announcement_2</c> (NL-878) until its node has a channel, replacing an older one.
    /// </summary>
    public bool AddNodeAnnouncement2(NodeAnnouncement2Message message, IPeerService? origin, out bool full)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Add(_nodes2, message.Payload.NodeId, message, origin, m => m.Payload.BlockHeight, out full);
    }

    /// <summary>Removes and returns the live <c>channel_update_2</c>s kept for <paramref name="shortChannelId"/>.</summary>
    public IReadOnlyList<OrphanEntry<ChannelUpdate2Message>> TakeUpdates2(ShortChannelId shortChannelId)
    {
        var taken = new List<OrphanEntry<ChannelUpdate2Message>>(2);
        lock (_lock)
        {
            for (byte direction = 0; direction <= 1; direction++)
            {
                if (_updates2.Remove((shortChannelId, direction), out var entry) && !IsExpired(entry))
                    taken.Add(entry);
            }
        }

        return taken;
    }

    /// <summary>True when a live <c>channel_update_2</c> is kept for <paramref name="shortChannelId"/>.</summary>
    public bool HasUpdates2(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            for (byte direction = 0; direction <= 1; direction++)
            {
                if (_updates2.TryGetValue((shortChannelId, direction), out var entry) && !IsExpired(entry))
                    return true;
            }

            return false;
        }
    }

    /// <summary>Removes and returns the live <c>node_announcement_2</c> kept for <paramref name="nodeId"/>.</summary>
    public OrphanEntry<NodeAnnouncement2Message>? TakeNodeAnnouncement2(CompactPubKey nodeId)
    {
        lock (_lock)
            return _nodes2.Remove(nodeId, out var entry) && !IsExpired(entry) ? entry : null;
    }

    private bool Add<TKey, TMessage>(Dictionary<TKey, OrphanEntry<TMessage>> entries, TKey key, TMessage message,
                                     IPeerService? origin, Func<TMessage, uint> order, out bool full)
        where TKey : notnull
    {
        full = false;
        lock (_lock)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                if (order(existing.Message) >= order(message) && !IsExpired(existing))
                    return false;

                entries[key] = new OrphanEntry<TMessage>(message, origin, _timeProvider.GetUtcNow());
                return true;
            }

            if (!HasRoom())
            {
                full = true;
                return false;
            }

            entries[key] = new OrphanEntry<TMessage>(message, origin, _timeProvider.GetUtcNow());
            return true;
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

    /// <summary>True when a live update is kept for <paramref name="shortChannelId"/>.</summary>
    public bool HasUpdates(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            for (byte direction = 0; direction <= 1; direction++)
            {
                if (_updates.TryGetValue((shortChannelId, direction), out var entry) && !IsExpired(entry))
                    return true;
            }

            return false;
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

    /// <summary>The node ids of the kept node announcements, expired ones included until the next prune.</summary>
    public IReadOnlyList<CompactPubKey> NodeIds
    {
        get
        {
            lock (_lock)
                return _nodes.Keys.ToList();
        }
    }

    /// <summary>
    /// Renews the wait of the announcement kept for <paramref name="nodeId"/> (NL-425): while the node's only channels
    /// are pending, its orphaned announcement must not expire with the TTL. False when none is kept.
    /// </summary>
    public bool RefreshNodeAnnouncement(CompactPubKey nodeId)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(nodeId, out var existing))
                return false;

            _nodes[nodeId] = existing with { AddedAt = _timeProvider.GetUtcNow() };
            return true;
        }
    }

    /// <summary>Drops the expired entries; returns how many.</summary>
    public int PruneExpired()
    {
        lock (_lock)
            return PruneExpiredLocked();
    }

    private bool HasRoom()
    {
        if (TotalLocked < _capacity)
            return true;

        PruneExpiredLocked();
        return TotalLocked < _capacity;
    }

    private int PruneExpiredLocked()
    {
        var removed = 0;
        foreach (var key in _updates.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList())
            removed += _updates.Remove(key) ? 1 : 0;
        foreach (var key in _nodes.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList())
            removed += _nodes.Remove(key) ? 1 : 0;
        foreach (var key in _updates2.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList())
            removed += _updates2.Remove(key) ? 1 : 0;
        foreach (var key in _nodes2.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList())
            removed += _nodes2.Remove(key) ? 1 : 0;
        return removed;
    }

    private bool IsExpired<T>(OrphanEntry<T> entry) => _timeProvider.GetUtcNow() - entry.AddedAt > _ttl;
}

/// <summary>A kept orphan message, the peer it came from (null for our own) and when it was kept.</summary>
public sealed record OrphanEntry<T>(T Message, IPeerService? Origin, DateTimeOffset AddedAt);