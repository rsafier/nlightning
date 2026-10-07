using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Gossip.Relay;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
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
using Sync;

/// <summary>
/// The relay of other nodes' taproot gossip (BOLTs PR #1059, NL-878): <c>channel_announcement_2</c>,
/// <c>channel_update_2</c> and <c>node_announcement_2</c>, only to connections that negotiated
/// <c>option_gossip_v2</c> and sent a <c>gossip_timestamp_filter</c> with a <c>block_height_range</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Collect:</b> with the BOLT 7 collect, the v2 slots the ingress accepted (or, when its feed overflowed, a pass over
/// the snapshot) are compared with the versions seen before (the block height) and what changed is added to the v2
/// pending set of every such connection (the newest version per key, at most
/// <see cref="GossipRelayOptions.MaxRelayPendingPerPeer"/>: the oldest goes first). Like the BOLT 7 path, the first
/// collect is a baseline and our own messages, spent channels and channels not checked against the chain are left
/// out (a channel of ours: only its 267, the peer's 271 is relayed as BOLT 7's is, NL-1145).
/// </para>
/// <para>
/// <b>Flush:</b> at the connection's BOLT 7 flush, in the order 267, 271, 269: a <c>channel_update_2</c> or
/// <c>node_announcement_2</c> goes out when its block height is inside the peer's <c>block_height_range</c> (the draft's
/// <c>first_block_height &lt;= block_height &lt; first_block_height + num_blocks</c>), a <c>channel_announcement_2</c>
/// (dated by its funding block, the draft) only with an update of its channel and never without one, and an update of a
/// channel whose announcement this connection never got brings it along; never back to the peer that sent us that
/// version. A full outbox (NL-360) stops the flush and keeps the rest for the next one; a connection that is gone loses
/// it. A peer that sent no <c>block_height_range</c> gets none of others' v2 gossip.
/// </para>
/// <para>
/// <b>Backlog:</b> a new filter with a <c>block_height_range</c> queues the v2 gossip of the graph inside it at the next
/// tick, sharing the BOLT 7 per-tick pacing budget and outbox stall handling (NL-1141).
/// </para>
/// </remarks>
public sealed partial class GossipRelayScheduler
{
    private readonly ConditionalWeakTable<IPeerService, V2RelayPeerState> _v2Peers = new();
    private readonly Dictionary<GossipMessageKey, uint> _knownV2 = [];

    private static bool IsV2Type(MessageTypes type) =>
        type is MessageTypes.ChannelAnnouncement2 or MessageTypes.ChannelUpdate2 or MessageTypes.NodeAnnouncement2;

    private static bool SupportsGossipV2(GossipPeer peer) => peer.Service.Features.OptionGossipV2 != FeatureSupport.No;

    /// <summary>
    /// The v2 versions in the graph for the accepted v2 slots (or the whole snapshot when the feed
    /// <paramref name="overflowed"/>), compared with what the relay saw before.
    /// </summary>
    private List<V2RelayItem> CollectV2(IReadOnlyList<GossipAcceptedKey> slots, bool overflowed)
    {
        var changed = new List<V2RelayItem>();
        if (overflowed)
        {
            var snapshot = _graphStore!.GetSnapshot();
            foreach (var channel in snapshot.Channels)
                AddChannelItems(channel, null);
            foreach (var node in snapshot.Nodes)
                if (NodeItem(node) is { } item)
                    See(item);
        }
        else
        {
            foreach (var slot in slots)
            {
                switch (slot.Type)
                {
                    case MessageTypes.ChannelAnnouncement2 or MessageTypes.ChannelUpdate2
                        when _graphStore!.TryGetChannel(slot.ShortChannelId, out var channel):
                        AddChannelItems(channel, slot.Type == MessageTypes.ChannelUpdate2 ? slot.Direction : null);
                        break;
                    case MessageTypes.NodeAnnouncement2
                        when _graphStore!.TryGetNode(slot.NodeId, out var node) && NodeItem(node) is { } item:
                        See(item);
                        break;
                }
            }
        }

        return changed;

        void AddChannelItems(GraphChannel channel, byte? onlyDirection)
        {
            if (!IsRelayableV2(channel))
                return;

            // A 267 counts as seen only once its channel has a relayable v2 update (it never goes out alone); the 267
            // of a channel of ours goes through the own path, but the peer's update of it is relayed (as BOLT 7's,
            // NL-1145)
            if (!IsOurs(channel.NodeId1) && !IsOurs(channel.NodeId2) && HasRelayablePolicyV2(channel))
                See(AnnouncementItem(channel));
            for (byte direction = 0; direction < 2; direction++)
            {
                if ((onlyDirection is null || onlyDirection == direction)
                 && GetRelayablePolicyV2(channel, direction) is { } policy)
                    See(UpdateItem(channel.ShortChannelId, direction, policy));
            }
        }

        void See(V2RelayItem item)
        {
            if (_knownV2.TryGetValue(item.Slot, out var version) && version == item.Height)
                return;

            _knownV2[item.Slot] = item.Height;
            changed.Add(item);
        }
    }

    /// <summary>Adds the collected v2 messages to every connection that asked for v2 gossip.</summary>
    private void QueueV2(IReadOnlyList<GossipPeer> peers, List<V2RelayItem> changed)
    {
        if (changed.Count == 0)
            return;

        foreach (var peer in peers)
        {
            if (!SupportsGossipV2(peer) || !IsOnOurChain(peer)
                                        || !_syncManager!.TryGetPeerBlockHeightRange(peer.Service, out _))
                continue;

            RecordV2Drops(GetV2State(peer).Add(changed, _relayOptions.MaxRelayPendingPerPeer));
        }
    }

    /// <summary>
    /// One connection's v2 flush (with its BOLT 7 flush): what is pending inside its
    /// <c>block_height_range</c>, 267s first, after its paced backlog ends.
    /// </summary>
    /// <returns>How many messages went out.</returns>
    private async Task<int> FlushV2PeerAsync(GossipPeer peer, RelayPeerState relayState, DateTimeOffset now)
    {
        if (!SupportsGossipV2(peer) || !_syncManager!.TryGetPeerBlockHeightRange(peer.Service, out var range))
            return 0;

        var state = GetV2State(peer);
        var pending = state.TakePending();
        if (pending.Count == 0)
            return 0;

        var items = SelectV2(pending, range, state, peer.NodeId);
        items.RemoveAll(state.ConsumeBacklogSent);
        var sent = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            GossipEnqueueResult result;
            try
            {
                result = await _sender.SendAsync(peer, item.ToMessage(), item.Raw.Length + sizeof(ushort));
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Could not relay {MessageType} to peer {Peer}", item.Type, peer.NodeId);
                return sent;
            }

            if (result == GossipEnqueueResult.Full)
            {
                // NL-360: the rest waits for the next flush, ahead of what is collected meanwhile
                RecordV2Drops(state.Add(items.Skip(i).ToList(), _relayOptions.MaxRelayPendingPerPeer));
                Pause(peer, relayState, now);
                return sent;
            }

            if (result != GossipEnqueueResult.Queued)
                return sent;

            if (item.Type == MessageTypes.ChannelAnnouncement2)
                state.MarkAnnounced(item.ShortChannelId);
            _metrics?.RecordRelayed(item.Type, "others");
            sent++;
        }

        return sent;
    }

    private async Task<int> SendV2BacklogAsync(GossipPeer peer, RelayPeerState relayState,
                                              DateTimeOffset now, int budget)
    {
        var state = GetV2State(peer);
        if (state.TakeBacklogRequest())
        {
            state.ClearWaiting();
            if (SupportsGossipV2(peer) && _syncManager!.TryGetPeerBlockHeightRange(peer.Service, out var range))
                state.Backlog = new Queue<V2RelayItem>(SelectV2(EnumerateV2Backlog(range), range, state, peer.NodeId));
        }

        var sent = 0;
        while (sent < budget && state.Backlog is { Count: > 0 } backlog)
        {
            var item = backlog.Peek();
            var result = await _sender.SendAsync(peer, item.ToMessage(), item.Raw.Length + sizeof(ushort));
            if (result == GossipEnqueueResult.Full)
            {
                Pause(peer, relayState, now);
                return sent;
            }

            if (result != GossipEnqueueResult.Queued)
            {
                state.ClearWaiting();
                return sent;
            }

            backlog.Dequeue();
            if (item.Type == MessageTypes.ChannelAnnouncement2)
                state.MarkAnnounced(item.ShortChannelId);
            state.RememberBacklogSent(item, _relayOptions.MaxRelayPendingPerPeer);
            _metrics?.RecordRelayed(item.Type, "others");
            sent++;
        }

        if (state.Backlog is { Count: 0 })
            state.Backlog = null;
        return sent;
    }

    /// <summary>
    /// What of <paramref name="pending"/> goes out now, in send order: updates and node announcements inside the
    /// range (not to their origin), the announcement of each channel they need (once per connection), announcements
    /// inside the range that have an update; nothing outside the range is kept.
    /// </summary>
    private List<V2RelayItem> SelectV2(List<V2RelayItem> pending, GossipBlockHeightRange range,
                                       V2RelayPeerState state, CompactPubKey peerId)
    {
        var announcements = new Dictionary<ShortChannelId, V2RelayItem>();
        var updates = new List<V2RelayItem>();
        var nodes = new List<V2RelayItem>();
        foreach (var item in pending)
        {
            if (!range.Includes(item.Height) || IsV2Origin(item, peerId))
                continue;

            switch (item.Type)
            {
                case MessageTypes.ChannelAnnouncement2:
                    announcements.TryAdd(item.ShortChannelId, item);
                    break;
                case MessageTypes.ChannelUpdate2:
                    updates.Add(item);
                    break;
                default:
                    nodes.Add(item);
                    break;
            }
        }

        // A 271 never goes out before its channel's 267 on this connection (it brings the 267 along), and a 267
        // never without an update (dropped otherwise; its update brings it later)
        var updatedChannels = updates.Select(u => u.ShortChannelId).ToHashSet();
        foreach (var shortChannelId in updatedChannels)
        {
            if (announcements.ContainsKey(shortChannelId) || state.WasAnnounced(shortChannelId))
                continue;

            // Ours goes through the own path (NL-1145)
            if (_graphStore!.TryGetChannel(shortChannelId, out var channel) && IsRelayableV2(channel)
                                                                           && !IsOurs(channel.NodeId1)
                                                                           && !IsOurs(channel.NodeId2))
                announcements[shortChannelId] = AnnouncementItem(channel);
        }

        var result = announcements.Values
                                  .Where(a => updatedChannels.Contains(a.ShortChannelId)
                                           || (_graphStore!.TryGetChannel(a.ShortChannelId, out var channel)
                                            && HasRelayablePolicyV2(channel)))
                                  .OrderBy(a => QueryResponder.ToUInt64(a.ShortChannelId))
                                  .ToList();
        result.AddRange(updates.OrderBy(u => QueryResponder.ToUInt64(u.ShortChannelId)).ThenBy(u => u.Direction));
        result.AddRange(nodes.Where(n => _graphStore!.NodeHasChannels(n.NodeId!.Value)));
        return result;
    }

    /// <summary>The graph's relayable v2 gossip inside <paramref name="range"/> (a new filter's backlog).</summary>
    private List<V2RelayItem> EnumerateV2Backlog(GossipBlockHeightRange range)
    {
        var items = new List<V2RelayItem>();
        var snapshot = _graphStore!.GetSnapshot();
        foreach (var channel in snapshot.Channels.OrderBy(c => QueryResponder.ToUInt64(c.ShortChannelId)))
        {
            if (!IsRelayableV2(channel) || !HasRelayablePolicyV2(channel))
                continue;

            var inside = new List<V2RelayItem>(2);
            for (byte direction = 0; direction < 2; direction++)
                if (GetRelayablePolicyV2(channel, direction) is { } policy && range.Includes(policy.Timestamp))
                    inside.Add(UpdateItem(channel.ShortChannelId, direction, policy));

            if (inside.Count == 0)
                continue;

            // The 267 of a channel of ours goes through the own path; the peer's update of it is relayed (NL-1145)
            if (!IsOurs(channel.NodeId1) && !IsOurs(channel.NodeId2))
                items.Add(AnnouncementItem(channel) with { Height = inside.Min(i => i.Height) });
            items.AddRange(inside);
        }

        foreach (var node in snapshot.Nodes)
            if (NodeItem(node) is { } item && range.Includes(item.Height))
                items.Add(item);

        return items;
    }

    /// <summary>
    /// A channel whose v2 gossip may go out: its <c>channel_announcement_2</c> kept, unspent, checked against the chain.
    /// </summary>
    private static bool IsRelayableV2(GraphChannel channel) =>
        channel.HasV2 && !channel.RawAnnouncement2.IsEmpty && channel.SpentAtHeight is null
     && channel.IsChainChecked;

    /// <summary>The <c>channel_update_2</c> of <paramref name="direction"/> when it may be relayed (not ours).</summary>
    private GraphPolicy? GetRelayablePolicyV2(GraphChannel channel, byte direction)
    {
        var policy = channel.GetPolicy(direction, 2);
        if (policy is null || policy.RawUpdate.IsEmpty)
            return null;

        return IsOurs(direction == 0 ? channel.NodeId1 : channel.NodeId2) ? null : policy;
    }

    private bool HasRelayablePolicyV2(GraphChannel channel) =>
        GetRelayablePolicyV2(channel, 0) is not null || GetRelayablePolicyV2(channel, 1) is not null;

    private V2RelayItem? NodeItem(GraphNode node) =>
        !node.HasV2 || node.RawAnnouncement2.IsEmpty || node.BlockHeight is not { } height || IsOurs(node.NodeId)
            ? null
            : new V2RelayItem(MessageTypes.NodeAnnouncement2, s_noChannel, 0, node.NodeId, height,
                              node.RawAnnouncement2);

    /// <summary>A <c>channel_announcement_2</c>, dated by its funding block (the draft).</summary>
    private static V2RelayItem AnnouncementItem(GraphChannel channel) =>
        new(MessageTypes.ChannelAnnouncement2, channel.ShortChannelId, 0, null, channel.ShortChannelId.BlockHeight,
            channel.RawAnnouncement2);

    private static V2RelayItem UpdateItem(ShortChannelId shortChannelId, byte direction, GraphPolicy policy) =>
        new(MessageTypes.ChannelUpdate2, shortChannelId, direction, null, policy.Timestamp, policy.RawUpdate);

    private bool IsV2Origin(V2RelayItem item, CompactPubKey peerId) =>
        _originTracker is not null && _originTracker.IsOrigin(item.VersionKey, peerId);

    private void RecordV2Drops(int dropped)
    {
        if (dropped > 0)
            _metrics?.RecordDropped(Metrics.GossipMetricReasons.RelayBacklogFull, dropped);
    }

    private V2RelayPeerState GetV2State(GossipPeer peer) => _v2Peers.GetValue(peer.Service, _ => new V2RelayPeerState());

    /// <summary>A new filter of a v2 peer asks for its v2 backlog at the next flush.</summary>
    private void RequestV2Backlog(GossipPeer peer)
    {
        if (SupportsGossipV2(peer))
            GetV2State(peer).RequestBacklog();
    }

    /// <summary>One v2 message waiting for a connection's flush.</summary>
    /// <param name="Type">267, 271 or 269.</param>
    /// <param name="ShortChannelId">The channel (267, 271).</param>
    /// <param name="Direction">The update's direction (271).</param>
    /// <param name="NodeId">The node (269).</param>
    /// <param name="Height">The block height it is dated by (a 267: its funding block).</param>
    /// <param name="Raw">The payload bytes, sent byte-exact.</param>
    private sealed record V2RelayItem(MessageTypes Type, ShortChannelId ShortChannelId, byte Direction,
                                      CompactPubKey? NodeId, uint Height, ReadOnlyMemory<byte> Raw)
    {
        /// <summary>The slot (one version per key).</summary>
        public GossipMessageKey Slot =>
            new(Type, QueryResponder.ToUInt64(ShortChannelId), Direction, NodeId, 0);

        /// <summary>The version, for origin suppression.</summary>
        public GossipMessageKey VersionKey =>
            new(Type, QueryResponder.ToUInt64(ShortChannelId), Direction, NodeId,
                Type == MessageTypes.ChannelAnnouncement2 ? 0 : Height);

        public IMessage ToMessage() => Type switch
        {
            MessageTypes.ChannelAnnouncement2 => new ChannelAnnouncement2Message(
                ChannelAnnouncement2Payload.Parse(Raw.Span)),
            MessageTypes.ChannelUpdate2 => new ChannelUpdate2Message(ChannelUpdate2Payload.Parse(Raw.Span)),
            _ => new NodeAnnouncement2Message(NodeAnnouncement2Payload.Parse(Raw.Span))
        };
    }

    /// <summary>A connection's v2 relay state (pending messages, announced channels, backlog request).</summary>
    private sealed class V2RelayPeerState
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<GossipMessageKey, (V2RelayItem Item, long Sequence)> _pending = [];
        private readonly HashSet<ShortChannelId> _announced = [];
        private long _sequence;
        private int _backlogRequested;

        public void RequestBacklog() => Interlocked.Exchange(ref _backlogRequested, 1);

        public bool TakeBacklogRequest() => Interlocked.Exchange(ref _backlogRequested, 0) == 1;

        /// <summary>Adds the newest version of each item; returns how many the bound evicted.</summary>
        public int Add(IReadOnlyList<V2RelayItem> items, int max)
        {
            var evicted = 0;
            lock (_lock)
            {
                foreach (var item in items)
                {
                    if (_pending.TryGetValue(item.Slot, out var current) && current.Item.Height > item.Height)
                        continue;

                    _pending[item.Slot] = (item, ++_sequence);
                }

                while (_pending.Count > Math.Max(1, max))
                {
                    var oldest = _pending.MinBy(p => p.Value.Sequence).Key;
                    _pending.Remove(oldest);
                    evicted++;
                }
            }

            return evicted;
        }

        public List<V2RelayItem> TakePending()
        {
            lock (_lock)
            {
                var items = _pending.Values.OrderBy(p => p.Sequence).Select(p => p.Item).ToList();
                _pending.Clear();
                return items;
            }
        }

        public Queue<V2RelayItem>? Backlog { get; set; }

        public bool HasBacklog => Backlog is { Count: > 0 };

        private readonly Dictionary<GossipMessageKey, uint> _backlogSent = [];

        public int PendingCount
        {
            get
            {
                lock (_lock)
                    return _pending.Count;
            }
        }

        public void RememberBacklogSent(V2RelayItem item, int max)
        {
            if (_backlogSent.Count >= 4 * Math.Max(max, 16))
                _backlogSent.Clear();
            _backlogSent[item.Slot] = item.Height;
        }

        public bool ConsumeBacklogSent(V2RelayItem item)
        {
            if (!_backlogSent.Remove(item.Slot, out var height))
                return false;
            return height >= item.Height;
        }

        public int ClearWaiting()
        {
            Backlog = null;
            _backlogSent.Clear();
            return TakePending().Count;
        }

        public void MarkAnnounced(ShortChannelId shortChannelId)
        {
            lock (_lock)
                _announced.Add(shortChannelId);
        }

        public bool WasAnnounced(ShortChannelId shortChannelId)
        {
            lock (_lock)
                return _announced.Contains(shortChannelId);
        }
    }
}