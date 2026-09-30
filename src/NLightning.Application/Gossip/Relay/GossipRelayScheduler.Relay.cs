using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Gossip.Relay;

using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
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
/// kept <see cref="GraphChannelVerification.Unverified"/> or <see cref="GraphChannelVerification.Assumed"/> (never
/// relayed, plan §3.4), <c>dont_forward</c> updates and
/// our own messages (the own path sends them to every peer regardless of filters).
/// </para>
/// <para>
/// <b>Flush:</b> each connection is flushed every <see cref="GossipRelayOptions.RelayFlushInterval"/> at its own phase
/// (staggered, from its node id). Only messages inside the peer's filter go out
/// (<see cref="GossipTimestampFilter.Includes"/>: <c>first &lt;= ts &lt; first + range</c>; a
/// <c>channel_announcement</c> takes the timestamps of its updates and goes out only when one of them is inside), all
/// <c>channel_announcement</c>s first, then the <c>channel_update</c>s, then the <c>node_announcement</c>s (of nodes
/// that still have a channel), never to a peer that sent us that version (origin suppression) and never to a peer
/// whose <c>init</c> networks exclude our chain. A peer that sent no filter gets nothing (B7-RL-01). An update of a
/// channel whose announcement this connection never got (its only update was outside the filter at an earlier flush)
/// brings the announcement with it (NL-368).
/// </para>
/// <para>
/// <b>Runs:</b> each connection's share of a tick runs on its own task; the tick waits at most
/// <see cref="GossipRelayOptions.RelaySendWait"/> for them, and a connection whose run is still sending (a stalled
/// transport write) is skipped by the later ticks until it ends, so one slow peer never stops the relay to the others.
/// </para>
/// <para>
/// <b>Bound:</b> each connection keeps at most <see cref="GossipRelayOptions.MaxRelayPendingPerPeer"/> messages
/// waiting for its flush; a new one beyond it drops the oldest waiting <c>node_announcement</c>, else the oldest
/// channel message, an announcement together with its waiting updates (counted in <see cref="GossipMetrics"/>), so a
/// peer that drains slowly never gets more than that queued on its outbox per flush. A later update of a channel whose
/// announcement was dropped goes out after that announcement again.
/// </para>
/// <para>
/// <b>Backlog:</b> a new filter asks for the graph inside it: one pass over the snapshot taken at the next tick (per
/// channel its announcement then its updates, then the node announcements), paced at
/// <see cref="GossipRelayOptions.BacklogMessagesPerSecond"/>; it replaces the pending set and any backlog still
/// running.
/// </para>
/// <para>
/// <b>Backpressure (NL-360):</b> the peer's outbox bounds its gossip share (<c>Gossip:MaxOutboxGossipPerPeer</c>,
/// <c>Gossip:MaxOutboxGossipBytesPerPeer</c>) and answers <see cref="GossipEnqueueResult.Full"/> when it is at the cap.
/// The relay then pauses that connection without losing its place: the backlog message that was refused is held and
/// sent first when the connection resumes (the snapshot pass continues after it), and the rest of an interrupted flush
/// goes back into the pending set ahead of what was collected since (oldest first when the bound evicts), so the
/// 256-before-258 order holds and no update goes out without its announcement. While paused, the connection gets no
/// flush and no backlog; it resumes when its outbox holds at most <see cref="GossipRelayOptions.RelayResumePercent"/>
/// of the caps. A paused connection that sends no gossip for <see cref="GossipRelayOptions.RelayStallTimeout"/> is
/// stalled: its backlog ends and its pending messages are dropped (counted as <c>relay_stalled</c>), so a peer that
/// never reads holds at most its outbox cap and the bounded pending set, and no old graph snapshot. The connection is
/// not closed (gossip is never worth a connection). No flush runs while a backlog does (NL-548): what was collected
/// meanwhile waits, so no update or node announcement reaches the peer before its channel's announcement. After a
/// stall the peer never got the channels past the backlog's position: a later update of such a channel goes out after
/// its announcement, and a node announcement only for a node with a channel the peer got.
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
    /// The relay's state for <c>describegraph</c> (NL-360, NL-375): messages waiting for the connections' flushes,
    /// paused connections, and the gossip waiting in the connected peers' outboxes.
    /// </summary>
    public GossipRelayStatus GetStatus()
    {
        var states = _relayPeers.Select(p => p.Value).ToList();
        long outboxMessages = 0, outboxBytes = 0;
        foreach (var peer in _peerDirectory.GetConnectedPeers())
        {
            if (_sender.GetDepth(peer) is not { } depth)
                continue;

            outboxMessages += depth.QueuedMessages;
            outboxBytes += depth.QueuedBytes;
        }

        return new GossipRelayStatus(IsRelayingOthers, states.Sum(s => (long)s.PendingCount),
                                     states.Count(s => s.IsPaused), outboxMessages, outboxBytes);
    }

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
                state.HeldBacklogItem = null;
                state.ResetBacklogPosition();
                state.Backlog = EnumerateBacklog(_graphStore!.GetSnapshot(), filter, peer.NodeId).GetEnumerator();
            }

            // NL-360: a connection paused on a full outbox waits until it drained (or stalls)
            if (state.IsPaused && !TryResume(peer, state, now))
                return 0;

            if (state.Backlog is not null)
            {
                sent += await SendBacklogAsync(peer, state, now);
                if (state.IsPaused)
                    return sent;
            }

            // NL-417: no flush while a backlog runs. What was collected since the snapshot includes newer updates and
            // node announcements of channels the backlog has not sent yet; flushed now they would reach the peer before
            // their channel_announcement. They wait in the pending set (bounded, a 256 with its 258s) and go out at the
            // first tick after the backlog ends
            if (now >= state.NextFlushAt && state.Backlog is null && state.HeldBacklogItem is null)
            {
                sent += await FlushPeerAsync(peer, state, filter, now);

                // A flush the full outbox interrupted goes on as soon as the connection resumes
                if (!state.IsPaused)
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

            RecordBacklogDrops(peer, GetRelayState(peer, _timeProvider.GetUtcNow()).AddPending(changed));
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

    private async Task<int> FlushPeerAsync(GossipPeer peer, RelayPeerState state, GossipTimestampFilter filter,
                                           DateTimeOffset now)
    {
        var pending = state.TakePending(AnnouncementToResend);
        if (state.TruncatedAt is { } cut)
            pending = CompleteAfterTruncatedBacklog(state, pending, cut);

        // NL-368: a 258 never goes out before its 256 on this connection. A 256 whose only update fell outside the
        // filter at an earlier flush was dropped here and counts as seen (never collected again), so the first
        // in-filter 258 of that channel brings the 256 with it
        var inBatch = new HashSet<Domain.Channels.ValueObjects.ShortChannelId>();
        foreach (var item in pending)
            if (item.Type == MessageTypes.ChannelAnnouncement)
                inBatch.Add(item.ShortChannelId);

        foreach (var shortChannelId in pending
                                         .Where(i => i.Type == MessageTypes.ChannelUpdate)
                                         .Select(i => i.ShortChannelId)
                                         .Distinct()
                                         .ToList())
        {
            if (inBatch.Contains(shortChannelId) || state.WasAnnounced(shortChannelId))
                continue;

            if (AnnouncementToResend(shortChannelId) is not { } announcement)
                continue;

            pending.Add(announcement);
            inBatch.Add(shortChannelId);
        }

        var items = pending
                         .OrderBy(i => i.Rank)
                         .ThenBy(i => QueryResponder.ToUInt64(i.ShortChannelId))
                         .ThenBy(i => i.Direction)
                         .ToList();
        if (items.Count == 0)
            return 0;

        var sent = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!ShouldRelay(item, filter, peer.NodeId))
                continue;

            switch (await TrySendAsync(peer, item))
            {
                case SendResult.Sent:
                    sent++;
                    if (item.Type == MessageTypes.ChannelAnnouncement)
                    {
                        state.RememberAnnounced(item.ShortChannelId);
                        state.RememberAnnouncedAfterCut(item.ShortChannelId);
                    }

                    break;
                case SendResult.Full:
                    // NL-360: the refused message and the rest go back, ahead of what was collected since
                    var dropped = state.PutBack(items.Skip(i).ToList());
                    RecordBacklogDrops(peer, dropped);
                    Pause(peer, state, now);
                    return sent;
                case SendResult.ConnectionGone:
                    return sent;
            }
        }

        return sent;
    }

    private async Task<int> SendBacklogAsync(GossipPeer peer, RelayPeerState state, DateTimeOffset now)
    {
        var budget = (int)Math.Max(1, Math.Min(int.MaxValue,
                                               _relayOptions.BacklogMessagesPerSecond
                                             * _relayOptions.RelayTickInterval.TotalSeconds));
        var sent = 0;
        while (sent < budget)
        {
            RelayItem item;
            if (state.HeldBacklogItem is { } held)
            {
                // Refused by the full outbox last time: it goes first, so the pass keeps its order
                item = held;
                state.HeldBacklogItem = null;
            }
            else if (!state.Backlog!.MoveNext())
            {
                state.Backlog.Dispose();
                state.Backlog = null;
                _logger.LogDebug("Sent the gossip backlog to peer {Peer}", peer.NodeId);
                break;
            }
            else
            {
                item = state.Backlog.Current;
            }

            switch (await TrySendAsync(peer, item))
            {
                case SendResult.Sent:
                    sent++;
                    state.AdvanceBacklogPosition(item);
                    if (item.Type == MessageTypes.ChannelAnnouncement)
                        state.RememberAnnounced(item.ShortChannelId);

                    break;
                case SendResult.Full:
                    state.HeldBacklogItem = item;
                    Pause(peer, state, now);
                    return sent;
                case SendResult.ConnectionGone:
                    state.EndBacklog();
                    return sent;
            }
        }

        return sent;
    }

    /// <summary>
    /// Pauses a connection whose outbox refused gossip (NL-360): no flush or backlog until it drained.
    /// </summary>
    private void Pause(GossipPeer peer, RelayPeerState state, DateTimeOffset now)
    {
        var depth = _sender.GetDepth(peer);
        state.Pause(now, depth?.SentMessages ?? 0);
        _metrics?.RecordRelayPaused();
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Paused the gossip relay to peer {Peer}: its outbox holds {Messages} gossip messages "
                           + "({Bytes} bytes)", peer.NodeId, depth?.QueuedMessages, depth?.QueuedBytes);
    }

    /// <summary>
    /// True when the paused connection's outbox drained to <see cref="GossipRelayOptions.RelayResumePercent"/> of its
    /// caps (or it has no outbox any more: the next send finds out); else checks it for a stall (NL-360).
    /// </summary>
    private bool TryResume(GossipPeer peer, RelayPeerState state, DateTimeOffset now)
    {
        if (_sender.GetDepth(peer) is not { } depth || depth.IsAtOrBelow(_relayOptions.RelayResumePercent))
        {
            state.Resume();
            _logger.LogDebug("Resumed the gossip relay to peer {Peer}", peer.NodeId);
            return true;
        }

        if (depth.SentMessages != state.ProgressMark)
        {
            state.MarkProgress(now, depth.SentMessages);
            return false;
        }

        if (_relayOptions.RelayStallTimeout <= TimeSpan.Zero || state.IsStalled
         || now - state.LastProgressAt < _relayOptions.RelayStallTimeout)
            return false;

        var dropped = state.Stall();
        _metrics?.RecordRelayStalled();
        _metrics?.RecordDropped(GossipMetricReasons.RelayStalled, dropped);
        _logger.LogInformation("Peer {Peer} read no gossip for {Timeout} with {Messages} gossip messages waiting: ended "
                             + "its gossip backlog and dropped the {Dropped} messages waiting for its relay flush",
                               peer.NodeId, _relayOptions.RelayStallTimeout, depth.QueuedMessages, dropped);
        return false;
    }

    private void RecordBacklogDrops(GossipPeer peer, int dropped)
    {
        if (dropped <= 0)
            return;

        _metrics?.RecordDropped(GossipMetricReasons.RelayBacklogFull, dropped);
        _logger.LogDebug("Dropped the {Count} oldest gossip messages waiting for peer {Peer}: its relay backlog holds "
                       + "{Max}", dropped, peer.NodeId, _relayOptions.MaxRelayPendingPerPeer);
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
            // NL-548: only a node with a channel the backlog announced (a spent channel stays in the graph for 72
            // blocks but is never relayed, and a node_announcement without a known channel is ignored, BOLT 7)
            if (node.RawAnnouncement.IsEmpty || IsOurs(node.NodeId) || !filter.Includes(node.Timestamp)
             || !snapshot.TryGetNodeIndex(node.NodeId, out var index)
             || !snapshot.GetAdjacency(index).Any(e => IsRelayable(e.Channel) && HasUpdateInside(e.Channel, filter)))
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
                return filter.Includes(item.Timestamp) && HasRelayableChannel(item.NodeId!.Value);
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
            // The wire size: the message type and the payload
            switch (await _sender.SendAsync(peer, message, item.Raw.Length + sizeof(ushort)))
            {
                case GossipEnqueueResult.Full:
                    return SendResult.Full;
                case GossipEnqueueResult.Gone:
                    return SendResult.ConnectionGone;
            }

            _metrics?.RecordRelayed(item.Type, "others");
            return SendResult.Sent;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not relay {MessageType} to peer {Peer}", item.Type, peer.NodeId);
            return SendResult.ConnectionGone;
        }
    }

    /// <summary>
    /// True when the node has a channel whose gossip may go out (NL-548): a node whose channels are all spent (kept in
    /// the graph for 72 blocks) or unverified has none the peer could know, so its announcement would be ignored.
    /// </summary>
    private bool HasRelayableChannel(CompactPubKey nodeId)
    {
        if (!_graphStore!.NodeHasChannels(nodeId))
            return false;

        var snapshot = _graphStore.GetSnapshot();
        return snapshot.TryGetNodeIndex(nodeId, out var index)
            && snapshot.GetAdjacency(index).Any(e => IsRelayable(e.Channel) && HasRelayablePolicy(e.Channel));
    }

    private bool IsOrigin(RelayItem item, CompactPubKey peerId) =>
        _originTracker is not null && _originTracker.IsOrigin(item.VersionKey, peerId);

    /// <summary>
    /// A channel whose gossip may go out: announced (raw bytes), unspent and checked against the chain (not kept
    /// <c>Unverified</c> or <c>Assumed</c>).
    /// </summary>
    private static bool IsRelayable(GraphChannel channel) =>
        !channel.RawAnnouncement.IsEmpty && channel.SpentAtHeight is null && channel.IsChainChecked;

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

    /// <summary>
    /// After a stall ended a backlog before its end (NL-548), the peer never got the channels after the backlog's
    /// position: a live <c>channel_update</c> of such a channel goes out after its <c>channel_announcement</c> (added
    /// here once per channel), and a <c>node_announcement</c> only for a node with a channel the peer got (else it is
    /// left out: BOLT 7 has the peer ignore it).
    /// </summary>
    private List<RelayItem> CompleteAfterTruncatedBacklog(RelayPeerState state, List<RelayItem> items, ulong cut)
    {
        bool Unsent(Domain.Channels.ValueObjects.ShortChannelId shortChannelId) =>
            QueryResponder.ToUInt64(shortChannelId) > cut && !state.WasAnnouncedAfterCut(shortChannelId);

        var announced = items.Where(i => i.Type == MessageTypes.ChannelAnnouncement)
                             .Select(i => i.ShortChannelId)
                             .ToHashSet();
        var orphaned = items.Where(i => i.Type == MessageTypes.ChannelUpdate && Unsent(i.ShortChannelId)
                                     && !announced.Contains(i.ShortChannelId))
                            .Select(i => i.ShortChannelId)
                            .Distinct()
                            .ToList();
        foreach (var shortChannelId in orphaned)
        {
            if (AnnouncementToResend(shortChannelId) is not { } announcement)
                continue;

            items.Add(announcement);
            announced.Add(shortChannelId);
        }

        if (!items.Any(i => i.Type == MessageTypes.NodeAnnouncement))
            return items;

        var snapshot = _graphStore!.GetSnapshot();
        items.RemoveAll(i => i.Type == MessageTypes.NodeAnnouncement
                          && !(snapshot.TryGetNodeIndex(i.NodeId!.Value, out var index)
                            && snapshot.GetAdjacency(index)
                                       .Any(e => IsRelayable(e.Channel)
                                              && (!Unsent(e.Channel.ShortChannelId)
                                               || announced.Contains(e.Channel.ShortChannelId)))));
        return items;
    }

    /// <summary>
    /// The stored announcement of a channel still relayable (null when it is not, or when the channel is ours: its
    /// announcement reaches every peer through the own path, regardless of filters, NL-368).
    /// </summary>
    private RelayItem? AnnouncementToResend(Domain.Channels.ValueObjects.ShortChannelId shortChannelId)
    {
        if (!_graphStore!.TryGetChannel(shortChannelId, out var channel)
         || !IsRelayable(channel)
         || IsOurs(channel.NodeId1)
         || IsOurs(channel.NodeId2))
            return null;

        return new RelayItem(MessageTypes.ChannelAnnouncement, shortChannelId, 0, null, 0, channel.RawAnnouncement);
    }

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
        Full,
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
    /// is touched only by the connection's run (<see cref="TryBeginRun"/>, one at a time).
    /// </summary>
    /// <remarks>
    /// The pending set holds at most <c>maxPending</c> messages (a replaced key counts as new). Beyond it the oldest
    /// <c>node_announcement</c> goes first, then the oldest channel message; a <c>channel_announcement</c> goes with
    /// its waiting updates (BOLT 7: an update of a channel the peer does not know is ignored), and its short channel id
    /// is remembered, so a later update of that channel is sent after the announcement again
    /// (<see cref="TakePending"/>): the collect saw the dropped announcement already and never offers it twice.
    /// </remarks>
    private sealed class RelayPeerState(DateTimeOffset nextFlushAt, int maxPending)
    {
        private readonly Lock _pendingLock = new();
        private readonly Dictionary<GossipMessageKey, (RelayItem Item, long Sequence)> _pending = [];
        private readonly Queue<(GossipMessageKey Slot, long Sequence)> _channelOrder = new();
        private readonly Queue<(GossipMessageKey Slot, long Sequence)> _nodeOrder = new();
        private readonly HashSet<Domain.Channels.ValueObjects.ShortChannelId> _unsentAnnouncements = [];
        private readonly Queue<Domain.Channels.ValueObjects.ShortChannelId> _unsentOrder = new();
        private readonly HashSet<Domain.Channels.ValueObjects.ShortChannelId> _announcedAfterCut = [];
        private long _sequence;
        private long _putBackSequence;
        private int _backlogRequested;
        private int _running;
        private int _paused;

        public DateTimeOffset NextFlushAt { get; set; } = nextFlushAt;
        public IEnumerator<RelayItem>? Backlog { get; set; }

        /// <summary>The backlog message the full outbox refused, sent first when the connection resumes (NL-360).</summary>
        public RelayItem? HeldBacklogItem { get; set; }

        /// <summary>The relay holds this connection paused on its full outbox (NL-360).</summary>
        public bool IsPaused => Volatile.Read(ref _paused) == 1;

        /// <summary>This pause already stalled (its backlog and pending messages are gone).</summary>
        public bool IsStalled { get; private set; }

        /// <summary>The outbox's sent-gossip count when the relay last saw it move.</summary>
        public long ProgressMark { get; private set; }

        /// <summary>When the relay last saw the paused outbox send gossip.</summary>
        public DateTimeOffset LastProgressAt { get; private set; }

        public void Pause(DateTimeOffset now, long sentMessages)
        {
            Volatile.Write(ref _paused, 1);
            IsStalled = false;
            MarkProgress(now, sentMessages);
        }

        public void MarkProgress(DateTimeOffset now, long sentMessages)
        {
            ProgressMark = sentMessages;
            LastProgressAt = now;
        }

        public void Resume()
        {
            Volatile.Write(ref _paused, 0);
            IsStalled = false;
        }

        /// <summary>Ends the backlog pass (its snapshot is released).</summary>
        public void EndBacklog()
        {
            Backlog?.Dispose();
            Backlog = null;
            HeldBacklogItem = null;
        }

        /// <summary>
        /// The paused connection stalled: ends its backlog and drops what waits for its flush; returns how many pending
        /// messages went (the rest of the backlog pass is not counted).
        /// </summary>
        public int Stall()
        {
            IsStalled = true;
            var held = HeldBacklogItem is null ? 0 : 1;
            if ((Backlog is not null || HeldBacklogItem is not null) && BacklogPosition != ulong.MaxValue)
            {
                TruncatedAt = BacklogPosition ?? 0;
                _announcedAfterCut.Clear();
            }

            EndBacklog();
            return held + ClearPending();
        }

        /// <summary>
        /// The short channel id (as a number) of the last channel the running backlog sent, <see cref="ulong.MaxValue"/>
        /// once it reached the node announcements; null before its first message.
        /// </summary>
        public ulong? BacklogPosition { get; private set; }

        /// <summary>
        /// Set when a stall ended the backlog early (NL-548): the peer never got the channels after this position.
        /// </summary>
        public ulong? TruncatedAt { get; private set; }

        public void AdvanceBacklogPosition(RelayItem item) =>
            BacklogPosition = item.Type == MessageTypes.NodeAnnouncement
                                  ? ulong.MaxValue
                                  : QueryResponder.ToUInt64(item.ShortChannelId);

        /// <summary>A new backlog: the peer gets the whole graph again.</summary>
        public void ResetBacklogPosition()
        {
            BacklogPosition = null;
            TruncatedAt = null;
            _announcedAfterCut.Clear();
        }

        /// <summary>A channel announcement past <see cref="TruncatedAt"/> went out (bounded; a forgotten one costs a
        /// repeated announcement, never an update without one).</summary>
        public void RememberAnnouncedAfterCut(Domain.Channels.ValueObjects.ShortChannelId shortChannelId)
        {
            if (TruncatedAt is null)
                return;

            if (_announcedAfterCut.Count >= 4 * Math.Max(maxPending, 16))
                _announcedAfterCut.Clear();
            _announcedAfterCut.Add(shortChannelId);
        }

        public bool WasAnnouncedAfterCut(Domain.Channels.ValueObjects.ShortChannelId shortChannelId) =>
            _announcedAfterCut.Contains(shortChannelId);

        private readonly HashSet<Domain.Channels.ValueObjects.ShortChannelId> _announced = [];
        private readonly Queue<Domain.Channels.ValueObjects.ShortChannelId> _announcedOrder = [];

        /// <summary>
        /// Remembers that this connection was sent the channel's <c>channel_announcement</c> (NL-368), so a later
        /// <c>channel_update</c> never brings it again. Bounded like <see cref="RememberAnnouncedAfterCut"/>: a
        /// forgotten one costs a repeated announcement, never an update without one.
        /// </summary>
        public void RememberAnnounced(Domain.Channels.ValueObjects.ShortChannelId shortChannelId)
        {
            if (_announced.Add(shortChannelId))
                _announcedOrder.Enqueue(shortChannelId);

            if (_announced.Count >= 4 * Math.Max(maxPending, 16))
            {
                _announced.Clear();
                _announcedOrder.Clear();
            }
        }

        public bool WasAnnounced(Domain.Channels.ValueObjects.ShortChannelId shortChannelId) =>
            _announced.Contains(shortChannelId);

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

        /// <summary>Queues the newest version per key; returns how many were dropped to stay in bounds.</summary>
        public int AddPending(IEnumerable<RelayItem> items)
        {
            lock (_pendingLock)
            {
                var dropped = 0;
                foreach (var item in items)
                {
                    var sequence = ++_sequence;
                    _pending[item.Slot] = (item, sequence);
                    (item.Type == MessageTypes.NodeAnnouncement ? _nodeOrder : _channelOrder)
                       .Enqueue((item.Slot, sequence));
                    if (item.Type == MessageTypes.ChannelAnnouncement)
                        _unsentAnnouncements.Remove(item.ShortChannelId);

                    while (_pending.Count > maxPending)
                    {
                        var evicted = EvictOne();
                        if (evicted == 0)
                            break;
                        dropped += evicted;
                    }
                }

                CompactOrder(_channelOrder);
                CompactOrder(_nodeOrder);
                return dropped;
            }
        }

        /// <summary>
        /// Takes the waiting messages. An update of a channel whose announcement was dropped here gets that
        /// announcement back (from <paramref name="announcementOf"/>, null when the channel left the graph).
        /// </summary>
        public List<RelayItem> TakePending(
            Func<Domain.Channels.ValueObjects.ShortChannelId, RelayItem?> announcementOf)
        {
            lock (_pendingLock)
            {
                var items = _pending.Values.Select(p => p.Item).ToList();
                if (_unsentAnnouncements.Count > 0)
                {
                    var announced = items.Where(i => i.Type == MessageTypes.ChannelAnnouncement)
                                         .Select(i => i.ShortChannelId)
                                         .ToHashSet();
                    var orphaned = items.Where(i => i.Type == MessageTypes.ChannelUpdate
                                                 && _unsentAnnouncements.Contains(i.ShortChannelId))
                                        .Select(i => i.ShortChannelId)
                                        .Distinct()
                                        .ToList();
                    foreach (var shortChannelId in orphaned)
                    {
                        _unsentAnnouncements.Remove(shortChannelId);
                        if (!announced.Contains(shortChannelId) && announcementOf(shortChannelId) is { } announcement)
                            items.Add(announcement);
                    }
                }

                _pending.Clear();
                _channelOrder.Clear();
                _nodeOrder.Clear();
                return items;
            }
        }

        /// <summary>
        /// Puts back the rest of a flush the full outbox interrupted (NL-360), ahead of everything collected since, so
        /// the bound evicts it first (it is the oldest); a key collected again meanwhile keeps its newer version.
        /// Returns how many messages the bound dropped.
        /// </summary>
        public int PutBack(IReadOnlyList<RelayItem> items)
        {
            lock (_pendingLock)
            {
                var channelFront = new List<(GossipMessageKey Slot, long Sequence)>();
                var nodeFront = new List<(GossipMessageKey Slot, long Sequence)>();
                foreach (var item in items)
                {
                    if (_pending.ContainsKey(item.Slot))
                        continue;

                    // Below every collected sequence (those count up from 1), so the order queues stay oldest first
                    var sequence = --_putBackSequence;
                    _pending[item.Slot] = (item, sequence);
                    (item.Type == MessageTypes.NodeAnnouncement ? nodeFront : channelFront).Add((item.Slot, sequence));
                    if (item.Type == MessageTypes.ChannelAnnouncement)
                        _unsentAnnouncements.Remove(item.ShortChannelId);
                }

                Prepend(_channelOrder, channelFront);
                Prepend(_nodeOrder, nodeFront);
                var dropped = 0;
                while (_pending.Count > maxPending)
                {
                    var evicted = EvictOne();
                    if (evicted == 0)
                        break;
                    dropped += evicted;
                }

                CompactOrder(_channelOrder);
                CompactOrder(_nodeOrder);
                return dropped;
            }
        }

        /// <summary>
        /// Drops what waits (a new backlog sends every announcement with its updates again); returns how many messages
        /// went.
        /// </summary>
        public int ClearPending()
        {
            lock (_pendingLock)
            {
                var count = _pending.Count;
                _pending.Clear();
                _channelOrder.Clear();
                _nodeOrder.Clear();
                _unsentAnnouncements.Clear();
                _unsentOrder.Clear();
                return count;
            }
        }

        private static void Prepend(Queue<(GossipMessageKey Slot, long Sequence)> order,
                                    List<(GossipMessageKey Slot, long Sequence)> front)
        {
            if (front.Count == 0)
                return;

            var rest = order.ToList();
            order.Clear();
            foreach (var entry in front)
                order.Enqueue(entry);
            foreach (var entry in rest)
                order.Enqueue(entry);
        }

        /// <summary>
        /// Drops the oldest <c>node_announcement</c>, else the oldest channel message (an announcement with its
        /// updates); returns how many messages went.
        /// </summary>
        private int EvictOne()
        {
            if (TryDequeueLive(_nodeOrder, out var slot))
            {
                _pending.Remove(slot);
                return 1;
            }

            if (!TryDequeueLive(_channelOrder, out slot))
                return 0;

            var item = _pending[slot].Item;
            _pending.Remove(slot);
            if (item.Type != MessageTypes.ChannelAnnouncement)
                return 1;

            var evicted = 1;
            for (byte direction = 0; direction < 2; direction++)
            {
                if (_pending.Remove(GossipMessageKey.ChannelUpdate(item.ShortChannelId, direction, 0)))
                    evicted++;
            }

            RememberUnsent(item.ShortChannelId);
            return evicted;
        }

        private bool TryDequeueLive(Queue<(GossipMessageKey Slot, long Sequence)> order, out GossipMessageKey slot)
        {
            while (order.TryDequeue(out var oldest))
            {
                // A stale order entry (its key was replaced or evicted since) removes nothing
                if (_pending.TryGetValue(oldest.Slot, out var live) && live.Sequence == oldest.Sequence)
                {
                    slot = oldest.Slot;
                    return true;
                }
            }

            slot = default;
            return false;
        }

        /// <summary>At most <c>maxPending</c> short channel ids, the oldest forgotten first.</summary>
        private void RememberUnsent(Domain.Channels.ValueObjects.ShortChannelId shortChannelId)
        {
            if (_unsentAnnouncements.Add(shortChannelId))
                _unsentOrder.Enqueue(shortChannelId);

            while (_unsentAnnouncements.Count > maxPending && _unsentOrder.TryDequeue(out var oldest))
                _unsentAnnouncements.Remove(oldest);

            if (_unsentOrder.Count > 2 * Math.Max(_unsentAnnouncements.Count, 16))
            {
                var live = _unsentOrder.Where(_unsentAnnouncements.Contains).Distinct().ToList();
                _unsentOrder.Clear();
                foreach (var entry in live)
                    _unsentOrder.Enqueue(entry);
            }
        }

        /// <summary>Drops the stale order entries once they outnumber the live ones (replaced keys).</summary>
        private void CompactOrder(Queue<(GossipMessageKey Slot, long Sequence)> order)
        {
            if (order.Count <= 2 * Math.Max(_pending.Count, 16))
                return;

            var live = order.Where(o => _pending.TryGetValue(o.Slot, out var p) && p.Sequence == o.Sequence).ToList();
            order.Clear();
            foreach (var entry in live)
                order.Enqueue(entry);
        }
    }
}