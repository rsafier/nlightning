namespace NLightning.Application.Gossip.Relay;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Which peers sent us which gossip (BOLT 7 plan G3-T3, origin suppression; B7-RL-01): the relay never sends a message
/// back to a peer that sent it to us. Keyed by what identifies a message's version (type, channel or node, direction,
/// timestamp), bounded: the oldest keys are forgotten first.
/// </summary>
public sealed class GossipOriginTracker
{
    /// <summary>The default number of message versions remembered.</summary>
    public const int DefaultCapacity = 100_000;

    private const int MaxOriginsPerMessage = 8;

    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly Dictionary<GossipMessageKey, List<CompactPubKey>> _origins = [];
    private readonly Queue<GossipMessageKey> _order = new();

    public GossipOriginTracker(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>The number of message versions remembered.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _origins.Count;
        }
    }

    /// <summary>Records that <paramref name="origin"/> sent us <paramref name="message"/> (256/257/258 only).</summary>
    public void Record(IMessage message, CompactPubKey origin)
    {
        if (KeyOf(message) is { } key)
            Record(key, origin);
    }

    /// <summary>Records that <paramref name="origin"/> sent us the message version <paramref name="key"/>.</summary>
    public void Record(GossipMessageKey key, CompactPubKey origin)
    {
        lock (_lock)
        {
            if (_origins.TryGetValue(key, out var origins))
            {
                if (origins.Count < MaxOriginsPerMessage && !origins.Contains(origin))
                    origins.Add(origin);
                return;
            }

            while (_origins.Count >= _capacity && _order.TryDequeue(out var oldest))
                _origins.Remove(oldest);

            _origins[key] = [origin];
            _order.Enqueue(key);
        }
    }

    /// <summary>True when <paramref name="peer"/> sent us the message version <paramref name="key"/>.</summary>
    public bool IsOrigin(GossipMessageKey key, CompactPubKey peer)
    {
        lock (_lock)
            return _origins.TryGetValue(key, out var origins) && origins.Contains(peer);
    }

    /// <summary>The version key of a 256/257/258, or null for any other message.</summary>
    public static GossipMessageKey? KeyOf(IMessage message) => message switch
    {
        ChannelAnnouncementMessage announcement => GossipMessageKey.ChannelAnnouncement(
            announcement.Payload.ShortChannelId),
        ChannelUpdateMessage update => GossipMessageKey.ChannelUpdate(update.Payload.ShortChannelId,
                                                                      update.Payload.Direction ? (byte)1 : (byte)0,
                                                                      update.Payload.Timestamp),
        NodeAnnouncementMessage announcement => GossipMessageKey.NodeAnnouncement(announcement.Payload.NodeId,
                                                                                  announcement.Payload.Timestamp),
        _ => null
    };
}

/// <summary>
/// One version of a gossip message: a <c>channel_announcement</c> by its channel, a <c>channel_update</c> by channel,
/// direction and timestamp, a <c>node_announcement</c> by node and timestamp.
/// </summary>
/// <param name="Type">The message type.</param>
/// <param name="ShortChannelId">The channel (256, 258), else 0.</param>
/// <param name="Direction">The update's direction (258), else 0.</param>
/// <param name="NodeId">The node (257), else null.</param>
/// <param name="Timestamp">The timestamp (257, 258), else 0.</param>
public readonly record struct GossipMessageKey(
    MessageTypes Type,
    ulong ShortChannelId,
    byte Direction,
    CompactPubKey? NodeId,
    uint Timestamp)
{
    /// <summary>The key of a <c>channel_announcement</c>.</summary>
    public static GossipMessageKey ChannelAnnouncement(Domain.Channels.ValueObjects.ShortChannelId shortChannelId) =>
        new(MessageTypes.ChannelAnnouncement, Sync.QueryResponder.ToUInt64(shortChannelId), 0, null, 0);

    /// <summary>The key of a <c>channel_update</c>.</summary>
    public static GossipMessageKey ChannelUpdate(Domain.Channels.ValueObjects.ShortChannelId shortChannelId,
                                                 byte direction, uint timestamp) =>
        new(MessageTypes.ChannelUpdate, Sync.QueryResponder.ToUInt64(shortChannelId), direction, null, timestamp);

    /// <summary>The key of a <c>node_announcement</c>.</summary>
    public static GossipMessageKey NodeAnnouncement(CompactPubKey nodeId, uint timestamp) =>
        new(MessageTypes.NodeAnnouncement, 0, 0, nodeId, timestamp);
}