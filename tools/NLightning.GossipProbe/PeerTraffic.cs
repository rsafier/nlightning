using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace NLightning.GossipProbe;

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
    private StreamWriter? _rangeLog;

    /// <summary>Starts logging every <c>reply_channel_range</c> header to <paramref name="path"/> (CSV).</summary>
    public void OpenRangeLog(string path)
    {
        lock (_rangeLogLock)
        {
            _rangeLog = new StreamWriter(path) { AutoFlush = true };
            _rangeLog.WriteLine("utc,peer,first_blocknum,number_of_blocks,end_blocknum,sync_complete,encoding,scids,"
                              + "first_scid_block,last_scid_block");
        }
    }

    /// <summary>Logs one <c>reply_channel_range</c> header.</summary>
    public void LogRange(CompactPubKey peer, uint first, uint number, bool complete, int encoding, int scids,
                         uint? firstScidBlock, uint? lastScidBlock)
    {
        lock (_rangeLogLock)
            _rangeLog?.WriteLine($"{DateTime.UtcNow:O},{GossipProbe.ProbeOptions.AliasOf(peer.ToString())},{first},"
                               + $"{number},{(ulong)first + number},{(complete ? 1 : 0)},{encoding},{scids},"
                               + $"{firstScidBlock},{lastScidBlock}");
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
            traffic.LogRange(peer.PeerPubKey, payload.FirstBlocknum, payload.NumberOfBlocks, payload.SyncComplete,
                             encoding, scids, firstBlock, lastBlock);
        }

        inner.HandleMessage(peer, message);
    }
}