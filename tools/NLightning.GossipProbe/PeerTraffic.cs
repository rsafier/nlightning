using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace NLightning.GossipProbe;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// What each peer handed to the gossip stack, by message type (256-258 through the ingress, 261-265 through the sync
/// service), and how many 256-258 the ingress refused at the door (full queues, a banned peer).
/// </summary>
public sealed class PeerTraffic
{
    private readonly ConcurrentDictionary<(CompactPubKey Peer, string Kind), long> _counts = new();
    private readonly Lock _rangeLogLock = new();
    private readonly ConcurrentDictionary<ShortChannelId, ChannelSeen> _channels = new();
    private StreamWriter? _rangeLog;

    /// <summary>What was received for one short channel id: who announced it first, its updates.</summary>
    public sealed class ChannelSeen
    {
        public string? FirstAnnouncer;
        public int Announcements;
        public int Updates;
        public uint NewestUpdateTimestamp;
    }

    /// <summary>Per short channel id, what arrived (for the diagnosis of channels left without a policy).</summary>
    public IReadOnlyDictionary<ShortChannelId, ChannelSeen> Channels => _channels;

    /// <summary>The relay run's recorder (<c>--relay-to</c>), null otherwise.</summary>
    public RelayRecorder? Relay { get; set; }

    public void RecordGossip(CompactPubKey peer, IMessage message)
    {
        Relay?.RecordReceived(peer, message);
        switch (message)
        {
            case ChannelAnnouncementMessage announcement:
                {
                    var seen = _channels.GetOrAdd(announcement.Payload.ShortChannelId, _ => new ChannelSeen());
                    lock (seen)
                    {
                        seen.Announcements++;
                        seen.FirstAnnouncer ??= ProbeOptions.AliasOf(peer.ToString());
                    }

                    break;
                }
            case ChannelUpdateMessage update:
                {
                    var seen = _channels.GetOrAdd(update.Payload.ShortChannelId, _ => new ChannelSeen());
                    lock (seen)
                    {
                        seen.Updates++;
                        seen.NewestUpdateTimestamp = Math.Max(seen.NewestUpdateTimestamp, update.Payload.Timestamp);
                    }

                    break;
                }
        }
    }

    /// <summary>Starts logging every <c>reply_channel_range</c> header to <paramref name="path"/> (CSV).</summary>
    public void OpenRangeLog(string path)
    {
        lock (_rangeLogLock)
        {
            _rangeLog = new StreamWriter(path) { AutoFlush = true };
            _rangeLog.WriteLine("utc,peer,first_blocknum,number_of_blocks,end_blocknum,sync_complete,encoding,scids,"
                              + "first_scid_block,last_scid_block,timestamps,stale_or_missing_14d,both_zero");
        }
    }

    /// <summary>Logs one <c>reply_channel_range</c> header.</summary>
    public void LogRange(CompactPubKey peer, uint first, uint number, bool complete, int encoding, int scids,
                         uint? firstScidBlock, uint? lastScidBlock, int? timestamps, int staleOrMissing, int bothZero)
    {
        lock (_rangeLogLock)
            _rangeLog?.WriteLine($"{DateTime.UtcNow:O},{GossipProbe.ProbeOptions.AliasOf(peer.ToString())},{first},"
                               + $"{number},{(ulong)first + number},{(complete ? 1 : 0)},{encoding},{scids},"
                               + $"{firstScidBlock},{lastScidBlock},{timestamps},{staleOrMissing},{bothZero}");
    }

    public void Count(CompactPubKey peer, string kind, long delta = 1) =>
        _counts.AddOrUpdate((peer, kind), delta, (_, value) => value + delta);

    public long Get(CompactPubKey peer, string kind) => _counts.GetValueOrDefault((peer, kind));

    public IReadOnlyDictionary<(CompactPubKey Peer, string Kind), long> Snapshot() =>
        new Dictionary<(CompactPubKey Peer, string Kind), long>(_counts);

    public static string KindOf(IMessage message) => message.Type switch
    {
        MessageTypes.ChannelAnnouncement => "channel_announcement",
        MessageTypes.NodeAnnouncement => "node_announcement",
        MessageTypes.ChannelUpdate => "channel_update",
        MessageTypes.QueryShortChannelIds => "query_short_channel_ids",
        MessageTypes.ReplyShortChannelIdsEnd => "reply_short_channel_ids_end",
        MessageTypes.QueryChannelRange => "query_channel_range",
        MessageTypes.ReplyChannelRange => "reply_channel_range",
        MessageTypes.GossipTimestampFilter => "gossip_timestamp_filter",
        _ => $"type_{(ushort)message.Type}"
    };
}

/// <summary>Counts what the peer services hand to the graph ingress, then passes it on.</summary>
public sealed class CountingGossipIngress(IGossipIngress inner, PeerTraffic traffic) : IGossipIngress
{
    public bool IsEnabled => inner.IsEnabled;

    public bool TryEnqueue(IPeerService origin, IMessage message)
    {
        var kind = PeerTraffic.KindOf(message);
        traffic.Count(origin.PeerPubKey, kind);
        traffic.RecordGossip(origin.PeerPubKey, message);
        var queued = inner.TryEnqueue(origin, message);
        if (!queued)
            traffic.Count(origin.PeerPubKey, kind + ".refused_at_door");
        return queued;
    }
}

/// <summary>Counts the gossip query messages each peer sends us, then passes them on.</summary>
public sealed class CountingGossipSyncService(IGossipSyncService inner, PeerTraffic traffic) : IGossipSyncService
{
    public void OnPeerInitialized(IPeerService peer)
    {
        traffic.Count(peer.PeerPubKey, "init");
        inner.OnPeerInitialized(peer);
    }

    public void HandleMessage(IPeerService peer, IMessage message)
    {
        traffic.Count(peer.PeerPubKey, PeerTraffic.KindOf(message));
        if (message is ReplyChannelRangeMessage range)
        {
            // The SCIDs a peer reports (encoding byte + 8 bytes each), and replies in the forbidden zlib encoding
            var payload = range.Payload;
            var encoded = payload.EncodedShortIds.Span;
            var encoding = encoded.Length > 0 ? encoded[0] : -1;
            var scids = encoding == 0 ? (encoded.Length - 1) / 8 : 0;
            uint? firstBlock = scids > 0 ? BinaryPrimitives.ReadUInt32BigEndian(encoded[1..]) >> 8 : null;
            uint? lastBlock = scids > 0 ? BinaryPrimitives.ReadUInt32BigEndian(encoded[(1 + (scids - 1) * 8)..]) >> 8 : null;
            if (encoding >= 0)
                traffic.Count(peer.PeerPubKey, $"reply_channel_range.encoding_{encoding}");
            if (scids > 0)
                traffic.Count(peer.PeerPubKey, "reply_channel_range.scids", scids);
            // The update timestamps (gossip_queries_ex): how many say "both updates older than two weeks or none"
            int? timestampCount = null;
            int staleOrMissing = 0, bothZero = 0;
            if (range.TimestampsTlv is { } tlv && tlv.Value.Length > 0 && tlv.Value[0] == 0)
            {
                var data = tlv.Value.AsSpan(1);
                timestampCount = data.Length / 8;
                var limit = (uint)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1_209_600);
                for (var i = 0; i + 8 <= data.Length; i += 8)
                {
                    var node1 = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
                    var node2 = BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]);
                    if (Math.Max(node1, node2) < limit)
                        staleOrMissing++;
                    if (node1 == 0 && node2 == 0)
                        bothZero++;
                }
            }

            traffic.LogRange(peer.PeerPubKey, payload.FirstBlocknum, payload.NumberOfBlocks, payload.SyncComplete,
                             encoding, scids, firstBlock, lastBlock, timestampCount, staleOrMissing, bothZero);
        }

        inner.HandleMessage(peer, message);
    }
}