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
/// Only the four signatures were checked, so an entry proves nothing about the chain: anyone can sign an announcement
/// for any short channel id with keys of its own. A short channel id therefore keeps up to
/// <see cref="MaxCandidatesPerChannel"/> different announcements (candidates), at most one per sending peer (a peer's
/// newer announcement replaces its own older one); a different announcement beyond that is refused. So a later forgery
/// never displaces the real announcement, and the first update promotes the candidate whose node signed it. When the
/// index is full, the peer holding the most entries loses its oldest one, so a peer flooding forged announcements
/// evicts its own entries once it holds the largest share. Bounded by a capacity (all candidates counted) and a TTL
/// from when the entry was added (an identical re-send does not renew it, so an announcement a peer keeps re-sending
/// still leaves after the TTL and comes back as a new entry). Not persisted: after a restart peers send them again.
/// Thread-safe.
/// </remarks>
public sealed class PendingAnnouncementIndex
{
    /// <summary>The most different announcements (from different peers) kept for one short channel id.</summary>
    public const int MaxCandidatesPerChannel = 4;

    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<ShortChannelId, List<Slot>> _entries = new();
    private readonly Dictionary<OriginKey, LinkedList<Slot>> _byOrigin = new();
    private readonly LinkedList<Slot> _byAge = new();

    public PendingAnnouncementIndex(int capacity, TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _ttl = ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The kept announcements, every candidate counted (expired ones included until the next prune).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _byAge.Count;
        }
    }

    /// <summary>The oldest live announcement kept for <paramref name="shortChannelId"/>, if any.</summary>
    public bool TryGet(ShortChannelId shortChannelId, [NotNullWhen(true)] out PendingAnnouncement? entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(shortChannelId, out var slots))
            {
                foreach (var slot in slots)
                {
                    if (IsExpired(slot.Entry))
                        continue;

                    entry = slot.Entry;
                    return true;
                }
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// The live announcements (candidates) kept for <paramref name="shortChannelId"/>, oldest first; empty when none.
    /// </summary>
    public IReadOnlyList<PendingAnnouncement> GetCandidates(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(shortChannelId, out var slots))
                return [];

            var live = new List<PendingAnnouncement>(slots.Count);
            foreach (var slot in slots)
            {
                if (!IsExpired(slot.Entry))
                    live.Add(slot.Entry);
            }

            return live;
        }
    }

    /// <summary>True when a live announcement is kept for <paramref name="shortChannelId"/>.</summary>
    public bool Contains(ShortChannelId shortChannelId) => TryGet(shortChannelId, out _);

    /// <summary>True when exactly these announcement bytes are kept (live) for <paramref name="shortChannelId"/>.</summary>
    public bool ContainsRaw(ShortChannelId shortChannelId, ReadOnlySpan<byte> raw)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(shortChannelId, out var slots))
                return false;

            foreach (var slot in slots)
            {
                if (!IsExpired(slot.Entry) && slot.Entry.Raw.AsSpan().SequenceEqual(raw))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Keeps <paramref name="entry"/> as a candidate for its short channel id, replacing the one its peer sent before;
    /// refused while <see cref="MaxCandidatesPerChannel"/> other peers' announcements are kept for it. When the index
    /// is full of live entries, the oldest entry of the peer holding the most makes room.
    /// </summary>
    public PendingAddOutcome Add(PendingAnnouncement entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_capacity == 0)
            return PendingAddOutcome.Refused;

        lock (_lock)
        {
            var origin = new OriginKey(entry.OriginNodeId);
            if (_entries.TryGetValue(entry.ShortChannelId, out var slots))
            {
                // The sender's own earlier candidate, and whatever expired, make room first
                foreach (var slot in slots.ToArray())
                {
                    if (slot.Origin == origin || IsExpired(slot.Entry))
                        RemoveLocked(slot);
                }

                if (_entries.TryGetValue(entry.ShortChannelId, out slots) && slots.Count >= MaxCandidatesPerChannel)
                    return PendingAddOutcome.Refused;
            }

            var evicted = false;
            if (_byAge.Count >= _capacity)
            {
                PruneExpiredLocked();
                if (_byAge.Count >= _capacity && LargestOriginLocked()?.First is { } oldest)
                {
                    RemoveLocked(oldest.Value);
                    evicted = true;
                }
            }

            var added = new Slot(entry, origin);
            added.AgeNode = _byAge.AddLast(added);
            if (!_byOrigin.TryGetValue(origin, out var ofOrigin))
                _byOrigin[origin] = ofOrigin = new LinkedList<Slot>();
            added.OriginNode = ofOrigin.AddLast(added);
            if (!_entries.TryGetValue(entry.ShortChannelId, out slots))
                _entries[entry.ShortChannelId] = slots = new List<Slot>(1);
            slots.Add(added);
            return evicted ? PendingAddOutcome.AddedWithEviction : PendingAddOutcome.Added;
        }
    }

    /// <summary>Removes <paramref name="entry"/> if it is still kept (the other candidates of its channel stay).</summary>
    public bool Remove(PendingAnnouncement entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (!_entries.TryGetValue(entry.ShortChannelId, out var slots))
                return false;

            foreach (var slot in slots)
            {
                if (!ReferenceEquals(slot.Entry, entry))
                    continue;

                RemoveLocked(slot);
                return true;
            }

            return false;
        }
    }

    /// <summary>Removes every candidate kept for <paramref name="shortChannelId"/>.</summary>
    public bool Remove(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(shortChannelId, out var slots))
                return false;

            foreach (var slot in slots.ToArray())
                RemoveLocked(slot);
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
        while (_byAge.First is { } oldest && IsExpired(oldest.Value.Entry))
        {
            RemoveLocked(oldest.Value);
            removed++;
        }

        return removed;
    }

    /// <summary>The entries of the peer holding the most (ties: the one whose oldest entry is older).</summary>
    private LinkedList<Slot>? LargestOriginLocked()
    {
        LinkedList<Slot>? largest = null;
        foreach (var slots in _byOrigin.Values)
        {
            if (largest is null || slots.Count > largest.Count
                                || (slots.Count == largest.Count
                                 && slots.First!.Value.Entry.AddedAt < largest.First!.Value.Entry.AddedAt))
                largest = slots;
        }

        return largest;
    }

    private void RemoveLocked(Slot slot)
    {
        _byAge.Remove(slot.AgeNode!);
        if (_byOrigin.TryGetValue(slot.Origin, out var ofOrigin))
        {
            ofOrigin.Remove(slot.OriginNode!);
            if (ofOrigin.Count == 0)
                _byOrigin.Remove(slot.Origin);
        }

        if (_entries.TryGetValue(slot.Entry.ShortChannelId, out var slots))
        {
            slots.Remove(slot);
            if (slots.Count == 0)
                _entries.Remove(slot.Entry.ShortChannelId);
        }
    }

    private bool IsExpired(PendingAnnouncement entry) => _timeProvider.GetUtcNow() - entry.AddedAt > _ttl;

    /// <summary>The peer that sent an entry (none for our own and the tests' announcements).</summary>
    private readonly record struct OriginKey(CompactPubKey? NodeId);

    private sealed class Slot(PendingAnnouncement entry, OriginKey origin)
    {
        public PendingAnnouncement Entry { get; } = entry;
        public OriginKey Origin { get; } = origin;
        public LinkedListNode<Slot>? AgeNode { get; set; }
        public LinkedListNode<Slot>? OriginNode { get; set; }
    }
}

/// <summary>What <see cref="PendingAnnouncementIndex.Add"/> did.</summary>
public enum PendingAddOutcome
{
    /// <summary>Kept.</summary>
    Added,

    /// <summary>Kept; the index was full, so an older entry (of the peer holding the most) was evicted.</summary>
    AddedWithEviction,

    /// <summary>
    /// Not kept: <see cref="PendingAnnouncementIndex.MaxCandidatesPerChannel"/> other peers' announcements wait for the
    /// same short channel id (or the index has no capacity).
    /// </summary>
    Refused
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