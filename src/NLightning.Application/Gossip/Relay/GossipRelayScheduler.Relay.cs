using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Gossip.Relay;

using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Graph.Interfaces;
using Interfaces;
using Metrics;
using Sync;
using Sync.Interfaces;

/// <summary>
/// The relay of other nodes' gossip (BOLT 7 plan §3.7, G3-T3; B7-Q-05, B7-RL-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>Collect:</b> every <see cref="GossipRelayOptions.RelayCollectInterval"/> the graph snapshot is compared with what
/// the relay saw before (per channel, update direction and node: the timestamp; a <c>channel_announcement</c> counts
/// as seen only once the channel has a relayable update, so it is queued with that update). What the ingress accepted
/// since goes
/// into the pending set of every connection that sent a <c>gossip_timestamp_filter</c>, the newest version per key
/// (a newer update replaces an older one). The first scan only records the graph as it is (a restart does not relay
/// the stored graph; each peer's filter asks for its backlog). Left out: spent channels (B7-Q-05 SHOULD NOT), channels
/// kept <see cref="GraphChannelVerification.Unverified"/> (never relayed, plan §3.4), <c>dont_forward</c> updates and
/// our own messages (the own path sends them to every peer regardless of filters).
/// </para>
/// <para>
/// <b>Flush:</b> each connection is flushed every <see cref="GossipRelayOptions.RelayFlushInterval"/> at its own phase
/// (staggered, from its node id). Only messages inside the peer's filter go out
/// (<see cref="GossipTimestampFilter.Includes"/>: <c>first &lt;= ts &lt; first + range</c>; a
/// <c>channel_announcement</c> takes the timestamps of its updates and goes out only when one of them is inside), all
/// <c>channel_announcement</c>s first, then the <c>channel_update</c>s, then the <c>node_announcement</c>s (of nodes
/// that still have a channel), never to a peer that sent us that version (origin suppression) and never to a peer
/// whose <c>init</c> networks exclude our chain. A peer that sent no filter gets nothing (B7-RL-01).
/// </para>
/// <para>
/// <b>Runs:</b> each connection's share of a tick runs on its own task; the tick waits at most
/// <see cref="GossipRelayOptions.RelaySendWait"/> for them, and a connection whose run is still sending (a stalled
/// transport write) is skipped by the later ticks until it ends, so one slow peer never stops the relay to the others.
/// </para>
/// <para>
/// <b>Bound:</b> each connection keeps at most <see cref="GossipRelayOptions.MaxRelayPendingPerPeer"/> messages
/// waiting for its flush; a new one beyond it drops the oldest waiting one (counted in <see cref="GossipMetrics"/>), so
/// a peer that drains slowly never gets more than that queued on its outbox per flush.
/// </para>
/// <para>
/// <b>Backlog:</b> a new filter asks for the graph inside it: one pass over the snapshot taken at the next tick (per
/// channel its announcement then its updates, then the node announcements), paced at
/// <see cref="GossipRelayOptions.BacklogMessagesPerSecond"/>; it replaces the pending set and any backlog still
/// running.
/// </para>
/// </remarks>
public sealed partial class GossipRelayScheduler
{
    /// <summary>The channel field of a node announcement's relay item (unused).</summary>
    private static readonly Domain.Channels.ValueObjects.ShortChannelId s_noChannel = new(0UL);

    private readonly IGraphStore? _graphStore;
    private readonly IGossipSyncManager? _syncManager;
    private readonly GossipOriginTracker? _originTracker;
    private readonly GossipRelayOptions _relayOptions;
    private readonly CompactPubKey? _ourNodeId;

    private readonly SemaphoreSlim _relayGate = new(1, 1);
    private readonly Dictionary<GossipMessageKey, KnownVersion> _known = [];
    private readonly ConditionalWeakTable<IPeerService, RelayPeerState> _relayPeers = new();
    private long _generation;
    private bool _baselined;
    private DateTimeOffset? _lastCollectAt;
    private ITimer? _relayTimer;

    /// <summary>True when other nodes' gossip is relayed (graph, sync manager and the relay switch).</summary>
    public bool IsRelayingOthers =>
        _graphStore is not null && _syncManager is not null
                                && _relayOptions.IsRelayEnabledFor(_nodeOptions.BitcoinNetwork);

    /// <summary>
    /// One relay tick (the relay timer calls it; tests call it directly): collects newly accepted gossip when due,
    /// then, per connected peer with a filter, sends a paced share of its backlog and flushes it when its phase is due.
    /// A tick that finds the previous one still running does nothing.
    /// </summary>
    /// <returns>How many messages were sent (or queued on the outboxes).</returns>
    internal async Task<int> RelayTickAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRelayingOthers || !await _relayGate.WaitAsync(0, cancellationToken))
            return 0;

        try
        {
            var now = _timeProvider.GetUtcNow();
            var peers = _peerDirectory.GetConnectedPeers();
            if (_lastCollectAt is not { } last || now - last >= _relayOptions.RelayCollectInterval)
            {
                Collect(peers);
                _lastCollectAt = now;
            }

            // Each peer runs on its own: a peer whose transport write stalls keeps its run (and later ticks skip it
            // until the run ends) but never holds up the others
            var runs = new List<Task<int>>();
            foreach (var peer in peers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // B7-RL-01: nothing of others before the peer's gossip_timestamp_filter; never off our chain
                if (!IsOnOurChain(peer) || !_syncManager!.TryGetPeerFilter(peer.Service, out var filter))
                    continue;

                var state = GetRelayState(peer, now);
                if (!state.TryBeginRun())
                    continue;

                runs.Add(RunPeerAsync(peer, state, filter, now));
            }

            if (runs.Count == 0)
                return 0;

            try
            {
                await Task.WhenAll(runs).WaitAsync(_relayOptions.RelaySendWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("{Count} gossip relay runs are still sending; their peers are skipped until they end",
                                 runs.Count(r => !r.IsCompleted));
            }

            return runs.Where(r => r.IsCompletedSuccessfully).Sum(r => r.Result);
        }
        finally
        {
            _relayGate.Release();
        }
    }

    /// <summary>
    /// One peer's share of a tick: a new backlog when its filter asked for one, a paced part of the backlog, and the
    /// flush when its phase is due. Runs while <paramref name="state"/> is marked running.
    /// </summary>
    private async Task<int> RunPeerAsync(GossipPeer peer, RelayPeerState state, GossipTimestampFilter filter,
                                         DateTimeOffset now)
    {
        try
        {
            var sent = 0;
            if (state.TakeBacklogRequest())
            {
                state.ClearPending();
                state.Backlog?.Dispose();
                state.Backlog = EnumerateBacklog(_graphStore!.GetSnapshot(), filter, peer.NodeId).GetEnumerator();
            }

            if (state.Backlog is not null)
                sent += await SendBacklogAsync(peer, state);

            if (now >= state.NextFlushAt)
            {
                sent += await FlushPeerAsync(peer, state, filter);
                while (state.NextFlushAt <= now)
                    state.NextFlushAt += _relayOptions.RelayFlushInterval;
            }

            return sent;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The gossip relay to peer {Peer} failed; the next tick retries", peer.NodeId);
            return 0;
        }
        finally
        {
            state.EndRun();
        }
    }

    /// <summary>
    /// Compares the graph with what the relay saw before and queues what changed for every connected peer with a
    /// filter (tests call it through <see cref="RelayTickAsync"/>).
    /// </summary>
    /// <returns>How many changed messages were found.</returns>
    private int Collect(IReadOnlyList<GossipPeer> peers)
    {
        var snapshot = _graphStore!.GetSnapshot();
        var generation = ++_generation;
        var changed = new List<RelayItem>();

        foreach (var channel in snapshot.Channels)
        {
            if (!IsRelayable(channel))
                continue;

            // A 256 counts as seen only once the channel has a relayable update: a 256 flushed alone is dropped (it
            // never goes out without an update), so marking it seen earlier would send its later 258 without it
            var weAreAnEnd = IsOurs(channel.NodeId1) || IsOurs(channel.NodeId2);
            if (!weAreAnEnd && HasRelayablePolicy(channel))
                See(GossipMessageKey.ChannelAnnouncement(channel.ShortChannelId), 1,
                    new RelayItem(MessageTypes.ChannelAnnouncement, channel.ShortChannelId, 0, null, 0,
                                  channel.RawAnnouncement));

            for (byte direction = 0; direction < 2; direction++)
            {
                if (GetRelayablePolicy(channel, direction) is not { } policy)
                    continue;

                See(GossipMessageKey.ChannelUpdate(channel.ShortChannelId, direction, 0), policy.Timestamp,
                    new RelayItem(MessageTypes.ChannelUpdate, channel.ShortChannelId, direction, null,
                                  policy.Timestamp, policy.RawUpdate));
            }
        }

        foreach (var node in snapshot.Nodes)
        {
            if (node.RawAnnouncement.IsEmpty || IsOurs(node.NodeId))
                continue;

            See(GossipMessageKey.NodeAnnouncement(node.NodeId, 0), node.Timestamp,
                new RelayItem(MessageTypes.NodeAnnouncement, s_noChannel, 0, node.NodeId, node.Timestamp,
                              node.RawAnnouncement));
        }

        // Forget what left the graph, so it counts as new if it comes back
        if (_known.Count > 0)
            foreach (var gone in _known.Where(k => k.Value.Generation != generation).Select(k => k.Key).ToList())
                _known.Remove(gone);

        if (!_baselined)
        {
            _baselined = true;
            return 0;
        }

        if (changed.Count == 0)
            return 0;

        foreach (var peer in peers)
        {
            if (!IsOnOurChain(peer) || !_syncManager!.TryGetPeerFilter(peer.Service, out _))
                continue;

            var dropped = GetRelayState(peer, _timeProvider.GetUtcNow()).AddPending(changed);
            if (dropped > 0)
            {
                _metrics?.RecordDropped(GossipMetricReasons.RelayBacklogFull, dropped);
                _logger.LogDebug("Dropped the {Count} oldest gossip messages waiting for peer {Peer}: its relay "
                               + "backlog holds {Max}", dropped, peer.NodeId, _relayOptions.MaxRelayPendingPerPeer);
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("{Count} gossip messages to relay at the next flushes", changed.Count);

        return changed.Count;

        void See(GossipMessageKey slot, uint version, RelayItem item)
        {
            if (_known.TryGetValue(slot, out var known) && known.Version == version)
            {
                _known[slot] = known with { Generation = generation };
                return;
            }

            _known[slot] = new KnownVersion(version, generation);
            changed.Add(item);
        }
    }

    private async Task<int> FlushPeerAsync(GossipPeer peer, RelayPeerState state, GossipTimestampFilter filter)
    {
        var items = state.TakePending()
                         .OrderBy(i => i.Rank)
                         .ThenBy(i => QueryResponder.ToUInt64(i.ShortChannelId))
                         .ThenBy(i => i.Direction)
                         .ToList();
        if (items.Count == 0)
            return 0;

        var sent = 0;
        foreach (var item in items)
        {
            if (!ShouldRelay(item, filter, peer.NodeId))
                continue;

            switch (await TrySendAsync(peer, item))
            {
                case SendResult.Sent:
                    sent++;
                    break;
                case SendResult.ConnectionGone:
                    return sent;
            }
        }

        return sent;
    }

    private async Task<int> SendBacklogAsync(GossipPeer peer, RelayPeerState state)
    {
        var budget = (int)Math.Max(1, Math.Min(int.MaxValue,
                                               _relayOptions.BacklogMessagesPerSecond
                                             * _relayOptions.RelayTickInterval.TotalSeconds));
        var sent = 0;
        while (sent < budget)
        {
            if (!state.Backlog!.MoveNext())
            {
                state.Backlog.Dispose();
                state.Backlog = null;
                _logger.LogDebug("Sent the gossip backlog to peer {Peer}", peer.NodeId);
                break;
            }

            switch (await TrySendAsync(peer, state.Backlog.Current))
            {
                case SendResult.Sent:
                    sent++;
                    break;
                case SendResult.ConnectionGone:
                    state.Backlog.Dispose();
                    state.Backlog = null;
                    return sent;
            }
        }

        return sent;
    }

    /// <summary>
    /// The graph inside <paramref name="filter"/>, lazily over one snapshot: per channel (by short channel id) its
    /// announcement, when one of its updates is inside, then those updates; then the announcements of the nodes that
    /// have a channel. Our own messages and what <paramref name="peerId"/> sent us are left out.
    /// </summary>
    private IEnumerable<RelayItem> EnumerateBacklog(IGraphView snapshot, GossipTimestampFilter filter,
                                                    CompactPubKey peerId)
    {
        foreach (var channel in snapshot.Channels.OrderBy(c => QueryResponder.ToUInt64(c.ShortChannelId)))
        {
            if (!IsRelayable(channel))
                continue;

            // B7-Q-05: the timestamp of a channel_announcement is that of its updates
            if (!HasUpdateInside(channel, filter))
                continue;

            if (!IsOurs(channel.NodeId1) && !IsOurs(channel.NodeId2))
            {
                var announcement = new RelayItem(MessageTypes.ChannelAnnouncement, channel.ShortChannelId, 0, null, 0,
                                                 channel.RawAnnouncement);
                if (!IsOrigin(announcement, peerId))
                    yield return announcement;
            }

            for (byte direction = 0; direction < 2; direction++)
            {
                if (GetRelayablePolicy(channel, direction) is not { } policy || !filter.Includes(policy.Timestamp))
                    continue;

                var update = new RelayItem(MessageTypes.ChannelUpdate, channel.ShortChannelId, direction, null,
                                           policy.Timestamp, policy.RawUpdate);
                if (!IsOrigin(update, peerId))
                    yield return update;
            }
        }

        foreach (var node in snapshot.Nodes)
        {
            if (node.RawAnnouncement.IsEmpty || IsOurs(node.NodeId) || !filter.Includes(node.Timestamp)
             || !snapshot.TryGetNodeIndex(node.NodeId, out var index) || snapshot.GetAdjacency(index).Count == 0)
                continue;

            var item = new RelayItem(MessageTypes.NodeAnnouncement, s_noChannel, 0, node.NodeId, node.Timestamp,
                                     node.RawAnnouncement);
            if (!IsOrigin(item, peerId))
                yield return item;
        }
    }

    /// <summary>The flush rules for one pending message and one peer (see the remarks of this class).</summary>
    private bool ShouldRelay(RelayItem item, GossipTimestampFilter filter, CompactPubKey peerId)
    {
        if (IsOrigin(item, peerId))
            return false;

        switch (item.Type)
        {
            case MessageTypes.ChannelAnnouncement:
                // Still in the graph, unspent, and at least one update inside the filter (never without an update)
                return _graphStore!.TryGetChannel(item.ShortChannelId, out var channel) && IsRelayable(channel)
                    && HasUpdateInside(channel, filter);
            case MessageTypes.ChannelUpdate:
                return filter.Includes(item.Timestamp)
                    && _graphStore!.TryGetChannel(item.ShortChannelId, out var updated) && IsRelayable(updated);
            case MessageTypes.NodeAnnouncement:
                return filter.Includes(item.Timestamp) && _graphStore!.NodeHasChannels(item.NodeId!.Value);
            default:
                return false;
        }
    }

    private async Task<SendResult> TrySendAsync(GossipPeer peer, RelayItem item)
    {
        IMessage message;
        try
        {
            message = item.ToMessage();
        }
        catch (Exception e)
        {
            // The graph only holds bytes that parsed once; a failure here is a bug, not the peer's fault
            _logger.LogWarning(e, "Could not rebuild a stored {MessageType} for relay", item.Type);
            return SendResult.Skipped;
        }

        try
        {
            if (!await _sender.SendAsync(peer, message))
                return SendResult.ConnectionGone;

            _metrics?.RecordRelayed(item.Type, "others");
            return SendResult.Sent;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not relay {MessageType} to peer {Peer}", item.Type, peer.NodeId);
            return SendResult.ConnectionGone;
        }
    }

    private bool IsOrigin(RelayItem item, CompactPubKey peerId) =>
        _originTracker is not null && _originTracker.IsOrigin(item.VersionKey, peerId);

    /// <summary>A channel whose gossip may go out: announced (raw bytes), unspent and not kept unverified.</summary>
    private static bool IsRelayable(GraphChannel channel) =>
        !channel.RawAnnouncement.IsEmpty && channel.SpentAtHeight is null
                                         && channel.Verification != GraphChannelVerification.Unverified;

    /// <summary>
    /// True when one of the channel's forwardable updates is inside <paramref name="filter"/> (B7-Q-05: the timestamp
    /// of a <c>channel_announcement</c> is that of its updates, and it never goes out without one).
    /// </summary>
    private static bool HasUpdateInside(GraphChannel channel, GossipTimestampFilter filter)
    {
        for (byte direction = 0; direction < 2; direction++)
            if (channel.GetPolicy(direction) is { DontForward: false } policy && !policy.RawUpdate.IsEmpty
                                                                              && filter.Includes(policy.Timestamp))
                return true;

        return false;
    }

    /// <summary>The policy of <paramref name="direction"/> when it may be relayed (raw bytes, not ours, forwardable).
    /// </summary>
    private GraphPolicy? GetRelayablePolicy(GraphChannel channel, byte direction)
    {
        var policy = channel.GetPolicy(direction);
        if (policy is null || policy.RawUpdate.IsEmpty || policy.DontForward)
            return null;

        // Direction 0 is node_id_1's update, 1 is node_id_2's: ours go through the own path
        return IsOurs(direction == 0 ? channel.NodeId1 : channel.NodeId2) ? null : policy;
    }

    private bool HasRelayablePolicy(GraphChannel channel) =>
        GetRelayablePolicy(channel, 0) is not null || GetRelayablePolicy(channel, 1) is not null;

    private RelayPeerState GetRelayState(GossipPeer peer, DateTimeOffset now) =>
        _relayPeers.GetValue(peer.Service, _ => new RelayPeerState(now + GetRelayPhase(peer.NodeId),
                                                                   _relayOptions.MaxRelayPendingPerPeer));

    /// <summary>The connection's offset in the flush interval (staggered flushes), stable per node id.</summary>
    internal TimeSpan GetRelayPhase(CompactPubKey nodeId)
    {
        var bytes = (byte[])nodeId;
        var hash = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, sizeof(uint)));
        return TimeSpan.FromTicks((long)(hash % (ulong)Math.Max(1, _relayOptions.RelayFlushInterval.Ticks)));
    }

    private void OnFilterReceived(object? sender, GossipFilterReceivedEventArgs args)
    {
        try
        {
            var peer = _peerDirectory.GetConnectedPeers().FirstOrDefault(p => ReferenceEquals(p.Service, args.Peer))
                    ?? new GossipPeer(args.Peer.PeerPubKey, args.Peer);
            GetRelayState(peer, _timeProvider.GetUtcNow()).RequestBacklog();
            StartRelayTimer();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not schedule the gossip backlog of peer {Peer}", args.Peer.PeerPubKey);
        }
    }

    private void StartRelayTimer()
    {
        lock (_lock)
        {
            if (_disposed || _relayTimer is not null)
                return;

            var tick = _relayOptions.RelayTickInterval;
            _relayTimer = _timeProvider.CreateTimer(_ => _ = RelayTickInBackgroundAsync(), null, tick, tick);
        }
    }

    private async Task RelayTickInBackgroundAsync()
    {
        try
        {
            await RelayTickAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "A gossip relay tick failed; the next one retries");
        }
    }

    private enum SendResult
    {
        Sent,
        Skipped,
        ConnectionGone
    }

    private readonly record struct KnownVersion(uint Version, long Generation);

    /// <summary>One message of another node, as stored in the graph.</summary>
    private sealed record RelayItem(
        MessageTypes Type,
        Domain.Channels.ValueObjects.ShortChannelId ShortChannelId,
        byte Direction,
        CompactPubKey? NodeId,
        uint Timestamp,
        ReadOnlyMemory<byte> Raw)
    {
        /// <summary>256 first, then 258, then 257.</summary>
        public int Rank => Type switch
        {
            MessageTypes.ChannelAnnouncement => ChannelAnnouncementRank,
            MessageTypes.ChannelUpdate => ChannelUpdateRank,
            _ => NodeAnnouncementRank
        };

        /// <summary>The pending-set key: the newest version per channel, update direction or node.</summary>
        public GossipMessageKey Slot => Type switch
        {
            MessageTypes.ChannelAnnouncement => GossipMessageKey.ChannelAnnouncement(ShortChannelId),
            MessageTypes.ChannelUpdate => GossipMessageKey.ChannelUpdate(ShortChannelId, Direction, 0),
            _ => GossipMessageKey.NodeAnnouncement(NodeId!.Value, 0)
        };

        /// <summary>The key of this version (origin suppression).</summary>
        public GossipMessageKey VersionKey => Type switch
        {
            MessageTypes.ChannelAnnouncement => GossipMessageKey.ChannelAnnouncement(ShortChannelId),
            MessageTypes.ChannelUpdate => GossipMessageKey.ChannelUpdate(ShortChannelId, Direction, Timestamp),
            _ => GossipMessageKey.NodeAnnouncement(NodeId!.Value, Timestamp)
        };

        public IMessage ToMessage() => Type switch
        {
            MessageTypes.ChannelAnnouncement => new ChannelAnnouncementMessage(
                ChannelAnnouncementPayload.Parse(Raw.Span)),
            MessageTypes.ChannelUpdate => new ChannelUpdateMessage(ChannelUpdatePayload.Parse(Raw.Span)),
            _ => new NodeAnnouncementMessage(NodeAnnouncementPayload.Parse(Raw.Span))
        };
    }

    /// <summary>
    /// The relay state of one connection. The pending set is shared with the collect (under its own lock); the rest
    /// is touched only by the connection's run (<see cref="TryBeginRun"/>, one at a time). The pending set holds at
    /// most <c>maxPending</c> messages, the oldest dropped first (a replaced key counts as new).
    /// </summary>
    private sealed class RelayPeerState(DateTimeOffset nextFlushAt, int maxPending)
    {
        private readonly Lock _pendingLock = new();
        private readonly Dictionary<GossipMessageKey, (RelayItem Item, long Sequence)> _pending = [];
        private readonly Queue<(GossipMessageKey Slot, long Sequence)> _order = new();
        private long _sequence;
        private int _backlogRequested;
        private int _running;

        public DateTimeOffset NextFlushAt { get; set; } = nextFlushAt;
        public IEnumerator<RelayItem>? Backlog { get; set; }

        public void RequestBacklog() => Interlocked.Exchange(ref _backlogRequested, 1);

        public bool TakeBacklogRequest() => Interlocked.Exchange(ref _backlogRequested, 0) == 1;

        /// <summary>Marks the connection's run started; false while the previous one is still sending.</summary>
        public bool TryBeginRun() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

        public void EndRun() => Volatile.Write(ref _running, 0);

        /// <summary>The number of waiting messages.</summary>
        public int PendingCount
        {
            get
            {
                lock (_pendingLock)
                    return _pending.Count;
            }
        }

        /// <summary>Queues the newest version per key; returns how many old ones were dropped to stay in bounds.</summary>
        public int AddPending(IEnumerable<RelayItem> items)
        {
            lock (_pendingLock)
            {
                var dropped = 0;
                foreach (var item in items)
                {
                    var sequence = ++_sequence;
                    _pending[item.Slot] = (item, sequence);
                    _order.Enqueue((item.Slot, sequence));
                    while (_pending.Count > maxPending && _order.TryDequeue(out var oldest))
                    {
                        // A stale order entry (its key was replaced since) removes nothing
                        if (_pending.TryGetValue(oldest.Slot, out var live) && live.Sequence == oldest.Sequence)
                        {
                            _pending.Remove(oldest.Slot);
                            dropped++;
                        }
                    }
                }

                CompactOrder();
                return dropped;
            }
        }

        public List<RelayItem> TakePending()
        {
            lock (_pendingLock)
            {
                var items = _pending.Values.Select(p => p.Item).ToList();
                ClearLocked();
                return items;
            }
        }

        public void ClearPending()
        {
            lock (_pendingLock)
                ClearLocked();
        }

        private void ClearLocked()
        {
            _pending.Clear();
            _order.Clear();
        }

        /// <summary>Drops the stale order entries once they outnumber the live ones (replaced keys).</summary>
        private void CompactOrder()
        {
            if (_order.Count <= 2 * Math.Max(_pending.Count, 16))
                return;

            var live = _order.Where(o => _pending.TryGetValue(o.Slot, out var p) && p.Sequence == o.Sequence).ToList();
            _order.Clear();
            foreach (var entry in live)
                _order.Enqueue(entry);
        }
    }
}