using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Gossip.Graph;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Protocol.Payloads;

/// <summary>
/// Signed <c>channel_announcement</c>s that no valid <c>channel_update</c> has followed yet (NL-406). BOLT 7: a node
/// "MUST NOT send the <c>channel_announcement</c>" without at least one <c>channel_update</c>, yet Core Lightning
/// streams about a quarter of the mainnet graph (long-dead channels) that way. They are kept here, outside the graph:
/// never routed over, served in query replies or relayed, never looked up on chain, and promoted into the graph by the
/// ingress when the first valid update arrives (the chain check runs then).
/// </summary>
/// <remarks>
/// Only the four signatures were checked, so an entry proves nothing about the chain: a later different announcement
/// for the same short channel id replaces it, and an update whose signature does not match its node is kept as an
/// orphan instead of counting against the peer. Bounded by a capacity (the oldest entry makes room) and a TTL from
/// when the entry was added (an identical re-send does not renew it, so an announcement a peer keeps re-sending still
/// leaves after the TTL and comes back as a new entry). Not persisted: after a restart peers send them again.
/// Thread-safe.
/// </remarks>
public sealed class PendingAnnouncementIndex
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<ShortChannelId, LinkedListNode<PendingAnnouncement>> _entries = new();
    private readonly LinkedList<PendingAnnouncement> _byAge = new();

    public PendingAnnouncementIndex(int capacity, TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _ttl = ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The kept announcements (expired ones included until the next prune).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>The live announcement kept for <paramref name="shortChannelId"/>, if any.</summary>
    public bool TryGet(ShortChannelId shortChannelId, [NotNullWhen(true)] out PendingAnnouncement? entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(shortChannelId, out var node) && !IsExpired(node.Value))
            {
                entry = node.Value;
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>True when a live announcement is kept for <paramref name="shortChannelId"/>.</summary>
    public bool Contains(ShortChannelId shortChannelId) => TryGet(shortChannelId, out _);

    /// <summary>
    /// Keeps <paramref name="entry"/>, replacing what is kept for its short channel id; when the index is full of live
    /// entries the oldest one makes room.
    /// </summary>
    /// <returns>True when an older live entry of another channel was evicted to make room.</returns>
    public bool AddOrReplace(PendingAnnouncement entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_capacity == 0)
            return false;

        lock (_lock)
        {
            if (_entries.TryGetValue(entry.ShortChannelId, out var existing))
                RemoveLocked(existing);

            var evicted = false;
            if (_entries.Count >= _capacity)
            {
                PruneExpiredLocked();
                if (_entries.Count >= _capacity && _byAge.First is { } oldest)
                {
                    RemoveLocked(oldest);
                    evicted = true;
                }
            }

            _entries[entry.ShortChannelId] = _byAge.AddLast(entry);
            return evicted;
        }
    }

    /// <summary>Removes <paramref name="entry"/> if it is still the one kept for its channel.</summary>
    public bool Remove(PendingAnnouncement entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (!_entries.TryGetValue(entry.ShortChannelId, out var node) || !ReferenceEquals(node.Value, entry))
                return false;

            RemoveLocked(node);
            return true;
        }
    }

    /// <summary>Removes whatever is kept for <paramref name="shortChannelId"/>.</summary>
    public bool Remove(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(shortChannelId, out var node))
                return false;

            RemoveLocked(node);
            return true;
        }
    }

    /// <summary>Drops the expired entries; returns how many.</summary>
    public int PruneExpired()
    {
        lock (_lock)
            return PruneExpiredLocked();
    }

    private int PruneExpiredLocked()
    {
        // Oldest first: stop at the first live entry
        var removed = 0;
        while (_byAge.First is { } oldest && IsExpired(oldest.Value))
        {
            RemoveLocked(oldest);
            removed++;
        }

        return removed;
    }

    private void RemoveLocked(LinkedListNode<PendingAnnouncement> node)
    {
        _byAge.Remove(node);
        _entries.Remove(node.Value.ShortChannelId);
    }

    private bool IsExpired(PendingAnnouncement entry) => _timeProvider.GetUtcNow() - entry.AddedAt > _ttl;
}

/// <summary>
/// A signed <c>channel_announcement</c> waiting for its first <c>channel_update</c>: its short channel id, its wire bytes
/// (only these are kept, about 430 bytes; the payload is parsed again when an update arrives), the node id of the peer
/// that sent it (not the connection, which may be long gone when the update arrives) and when it was kept.
/// </summary>
public sealed record PendingAnnouncement(ShortChannelId ShortChannelId, byte[] Raw, CompactPubKey? OriginNodeId,
                                         DateTimeOffset AddedAt)
{
    /// <summary>The announcement, parsed from <see cref="Raw"/>.</summary>
    public ChannelAnnouncementPayload ParseAnnouncement() => ChannelAnnouncementPayload.Parse(Raw);

    /// <summary>
    /// The channel as the announcement describes it, not checked on chain (no capacity): what a first update is
    /// validated against before the chain lookup.
    /// </summary>
    public GraphChannel ToUncheckedChannel() => ToUncheckedChannel(ParseAnnouncement());

    /// <summary><see cref="ToUncheckedChannel()"/> from the already parsed <paramref name="announcement"/>.</summary>
    public GraphChannel ToUncheckedChannel(ChannelAnnouncementPayload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        return new GraphChannel(announcement.ShortChannelId, announcement.NodeId1, announcement.NodeId2,
                                announcement.BitcoinKey1, announcement.BitcoinKey2, null, announcement.Features,
                                GraphChannelVerification.Unverified)
        {
            RawAnnouncement = Raw
        };
    }
}