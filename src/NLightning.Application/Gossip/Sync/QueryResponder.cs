using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Sync;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Graph.Interfaces;

/// <summary>
/// Builds the BOLT 7 answers to a peer's gossip queries from the graph (plan BOLT7 G3-T1, G3-T4; replaces the
/// "know nothing" <c>GossipQueryResponder</c> of the peer service, GG9).
/// </summary>
/// <remarks>
/// <para>
/// Only channels we would relay are served: announced (raw bytes kept), not spent, not <c>Unverified</c>. A range reply
/// lists only channels with at least one <c>channel_update</c> (BOLT 7 never sends an announcement without one to a
/// filter, and a querier could not use it).
/// </para>
/// <para>
/// <c>reply_channel_range</c>: the channels in the queried blocks in ascending order, chunked so each message fits
/// 65,535 bytes (at most <see cref="GossipSyncOptions.MaxScidsPerReply"/> ids, fewer with timestamps and checksums).
/// The first reply starts at the query's <c>first_blocknum</c>, each reply covers the blocks up to its last channel,
/// the next starts after it (or on the same block when a block's channels are split), and the last reaches the
/// query's end with <c>sync_complete</c> = 1 (B7-Q-04). <c>query_option</c> bit 0 adds <c>timestamps_tlv</c>, bit 1
/// <c>checksums_tlv</c> (CRC32C of each update without signature and timestamp, <see cref="ChannelUpdateChecksum"/>).
/// </para>
/// <para>
/// <c>query_short_channel_ids</c>: for each known channel, in query order, what <c>query_flags</c> asks for (without
/// flags: the announcement, both updates, then both node announcements), each node announcement at most once per
/// reply, then <c>reply_short_channel_ids_end</c> (B7-Q-02). Encoding 1 (zlib) and malformed fields are a
/// <see cref="WarningException"/> (plan D5).
/// </para>
/// <para>
/// Taproot gossip (BOLTs PR #1059's BOLT 7 query changes, NL-878), only for a requester that negotiated
/// <c>option_gossip_v2</c> (<c>includeV2</c>): a channel announced with <c>channel_announcement_2</c> is listed too
/// (the draft: "If the sender of <c>query_channel_range</c> has not advertised [it] then the response MUST only
/// include short channel IDs for channels announced with the <c>channel_announcement</c> message"), with the block
/// heights of its <c>channel_update_2</c>s in <c>timestamps_tlv</c> and their checksums (CRC32C of the update without
/// its signature and block height) in <c>checksums_tlv</c>; a scid query about it is answered with the v2 messages
/// (267, 271, 269, or the node's <c>node_announcement</c> when it has no v2 one). A channel announced both ways is
/// described by its v2 side to such a requester.
/// </para>
/// </remarks>
public sealed class QueryResponder
{
    /// <summary>
    /// The bytes of a <c>reply_channel_range</c> besides its short channel ids and the per-id TLV entries: type,
    /// chain hash, first block, block count, sync_complete, len, the encoding byte, and each TLV's type, length and
    /// encoding byte (with room to spare).
    /// </summary>
    internal const int ReplyOverhead = 64;

    private readonly IGraphStore _graphStore;
    private readonly GossipSyncOptions _options;
    private readonly Lock _sortedLock = new();
    private IGraphView? _sortedSnapshot;
    private GraphChannel[] _sortedChannels = [];
    private IGraphView? _sortedSnapshotV2;
    private GraphChannel[] _sortedChannelsV2 = [];

    public QueryResponder(IGraphStore graphStore, IOptions<GossipSyncOptions>? options = null)
    {
        _graphStore = graphStore;
        _options = options?.Value ?? new GossipSyncOptions();
    }

    /// <summary>
    /// The <c>reply_channel_range</c> messages answering <paramref name="query"/>, in send order (at least one; the
    /// last has <c>sync_complete</c> set).
    /// </summary>
    /// <exception cref="WarningException">The <c>query_option</c> TLV is malformed.</exception>
    /// <param name="query">The query.</param>
    /// <param name="ourChain">Our chain.</param>
    /// <param name="includeV2">The requester negotiated <c>option_gossip_v2</c> (NL-878).</param>
    public IReadOnlyList<ReplyChannelRangeMessage> CreateRangeReplies(QueryChannelRangeMessage query,
                                                                      ChainHash ourChain, bool includeV2 = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        var payload = query.Payload;
        var queryOption = query.QueryOptionTlv is null ? 0 : GossipQueryCodec.DecodeQueryOption(query.QueryOptionTlv.Value);
        var withTimestamps = (queryOption & GossipQueryCodec.QueryOptionTimestamps) != 0;
        var withChecksums = (queryOption & GossipQueryCodec.QueryOptionChecksums) != 0;

        // BOLT 7: number_of_blocks is at least 1; the end is capped so first + number fits a u32
        var first = payload.FirstBlocknum;
        var end = Math.Min((ulong)first + Math.Max(payload.NumberOfBlocks, 1u), uint.MaxValue);
        if (end <= first)
            end = (ulong)first + 1;

        var channels = payload.ChainHash == ourChain ? GetServedChannels(first, end, includeV2) : [];
        var perScid = ShortChannelId.Length + (withTimestamps ? GossipQueryCodec.PerChannelPairLength : 0)
                                            + (withChecksums ? GossipQueryCodec.PerChannelPairLength : 0);
        var maxPerReply = Math.Max(1, Math.Min(_options.MaxScidsPerReply,
                                               (GossipSyncOptions.MaxMessageLength - ReplyOverhead) / perScid));

        var replies = new List<ReplyChannelRangeMessage>();
        ulong start = first;
        var index = 0;
        while (true)
        {
            var take = Math.Min(maxPerReply, channels.Count - index);
            var chunk = channels.Skip(index).Take(take).ToList();
            index += take;
            if (index >= channels.Count)
            {
                replies.Add(CreateReply(payload.ChainHash, (uint)start, (uint)(end - start), true, chunk,
                                        withTimestamps, withChecksums, includeV2));
                return replies;
            }

            var lastHeight = chunk[^1].ShortChannelId.BlockHeight;
            replies.Add(CreateReply(payload.ChainHash, (uint)start, (uint)(lastHeight - start + 1), false, chunk,
                                    withTimestamps, withChecksums, includeV2));

            // A block whose channels do not fit one reply continues in the next one (BOLT 7: MAY split block
            // contents; first_blocknum stays non-decreasing)
            start = channels[index].ShortChannelId.BlockHeight == lastHeight ? lastHeight : lastHeight + 1UL;
        }
    }

    /// <summary>
    /// The messages answering <paramref name="query"/>, in send order: the requested announcements and updates, then
    /// <c>reply_short_channel_ids_end</c>.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="ourChain">Our chain; a query for another one gets only the end marker with
    /// <c>full_information</c> = 0.</param>
    /// <param name="fullInformation">Whether we keep up-to-date information (our initial sync with at least one peer
    /// completed).</param>
    /// <exception cref="WarningException">The query is malformed (unknown or zlib encoding, a partial short channel
    /// id, bad <c>query_flags</c>).</exception>
    /// <param name="includeV2">The requester negotiated <c>option_gossip_v2</c> (NL-878): channels announced with
    /// <c>channel_announcement_2</c> are answered with their v2 messages.</param>
    public IReadOnlyList<IMessage> CreateShortChannelIdsReplies(QueryShortChannelIdsMessage query, ChainHash ourChain,
                                                                bool fullInformation, bool includeV2 = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        var shortChannelIds = GossipQueryCodec.DecodeShortChannelIds(query.Payload.EncodedShortIds.Span,
                                                                     "query_short_channel_ids");
        var flags = query.QueryFlagsTlv is null
                        ? null
                        : GossipQueryCodec.DecodeQueryFlags(query.QueryFlagsTlv.Value, shortChannelIds.Length);

        var replies = new List<IMessage>();
        if (query.Payload.ChainHash != ourChain)
        {
            replies.Add(CreateEnd(query.Payload.ChainHash, false));
            return replies;
        }

        var sentNodes = new HashSet<CompactPubKey>();
        for (var i = 0; i < shortChannelIds.Length; i++)
        {
            if (!_graphStore.TryGetChannel(shortChannelIds[i], out var channel))
                continue;

            if (includeV2 && (IsListedAsV2(channel) || (IsServedV2(channel) && !IsServed(channel))))
            {
                AddV2Replies(channel, flags?[i] ?? GossipQueryCodec.QueryFlagAll, replies, sentNodes);
                continue;
            }

            if (!IsServed(channel))
                continue;

            var flag = flags?[i] ?? GossipQueryCodec.QueryFlagAll;
            if ((flag & GossipQueryCodec.QueryFlagChannelAnnouncement) != 0)
                replies.Add(new ChannelAnnouncementMessage(
                                ChannelAnnouncementPayload.Parse(channel.RawAnnouncement.Span)));
            if ((flag & GossipQueryCodec.QueryFlagChannelUpdate1) != 0 && ToMessage(channel.Policy1) is { } update1)
                replies.Add(update1);
            if ((flag & GossipQueryCodec.QueryFlagChannelUpdate2) != 0 && ToMessage(channel.Policy2) is { } update2)
                replies.Add(update2);
            if ((flag & GossipQueryCodec.QueryFlagNodeAnnouncement1) != 0)
                AddNode(channel.NodeId1, replies, sentNodes);
            if ((flag & GossipQueryCodec.QueryFlagNodeAnnouncement2) != 0)
                AddNode(channel.NodeId2, replies, sentNodes);
        }

        replies.Add(CreateEnd(query.Payload.ChainHash, fullInformation));
        return replies;
    }

    /// <summary>
    /// The served channels with <c>first &lt;= block &lt; end</c>, in ascending short channel id order.
    /// </summary>
    internal IReadOnlyList<GraphChannel> GetServedChannels(ulong first, ulong end, bool includeV2 = false)
    {
        var sorted = includeV2 ? GetSortedServedChannelsV2() : GetSortedServedChannels();
        var low = LowerBound(sorted, first);
        var high = LowerBound(sorted, end);
        return new ArraySegment<GraphChannel>(sorted, low, high - low);
    }

    /// <summary>
    /// True when the channel is one we relay: its announcement is kept, its funding output is unspent and it was
    /// verified against the chain (or is ours; never <c>Unverified</c> or <c>Assumed</c>).
    /// </summary>
    internal static bool IsServed(GraphChannel channel) =>
        !channel.RawAnnouncement.IsEmpty
     && channel.SpentAtHeight is null
     && channel.IsChainChecked;

    private GraphChannel[] GetSortedServedChannels()
    {
        // The snapshot is rebuilt only after a change, so the sorted list is too
        var snapshot = _graphStore.GetSnapshot();
        lock (_sortedLock)
        {
            if (!ReferenceEquals(snapshot, _sortedSnapshot))
            {
                _sortedChannels = snapshot.Channels
                                          .Where(c => IsServed(c) && HasRawUpdate(c))
                                          .OrderBy(c => ToUInt64(c.ShortChannelId))
                                          .ToArray();
                _sortedSnapshot = snapshot;
            }

            return _sortedChannels;
        }
    }

    /// <summary>
    /// True when the channel's <c>channel_announcement_2</c> is one we relay (NL-878): kept, unspent, checked against
    /// the chain (or ours).
    /// </summary>
    internal static bool IsServedV2(GraphChannel channel) =>
        channel.HasV2 && !channel.RawAnnouncement2.IsEmpty && channel.SpentAtHeight is null && channel.IsChainChecked;

    /// <summary>True when the channel is listed to a v2 requester with its v2 side.</summary>
    private static bool IsListedAsV2(GraphChannel channel) => IsServedV2(channel) && HasRawUpdateV2(channel);

    /// <summary>
    /// The channels a requester that negotiated <c>option_gossip_v2</c> is told about (NL-878): those served by
    /// either protocol with an update of that protocol, in ascending short channel id order.
    /// </summary>
    private GraphChannel[] GetSortedServedChannelsV2()
    {
        var snapshot = _graphStore.GetSnapshot();
        lock (_sortedLock)
        {
            if (!ReferenceEquals(snapshot, _sortedSnapshotV2))
            {
                _sortedChannelsV2 = snapshot.Channels
                                            .Where(c => IsListedAsV2(c) || (IsServed(c) && HasRawUpdate(c)))
                                            .OrderBy(c => ToUInt64(c.ShortChannelId))
                                            .ToArray();
                _sortedSnapshotV2 = snapshot;
            }

            return _sortedChannelsV2;
        }
    }

    private static bool HasRawUpdateV2(GraphChannel channel) =>
        channel.Policy1V2 is { RawUpdate.IsEmpty: false } || channel.Policy2V2 is { RawUpdate.IsEmpty: false };

    /// <summary>
    /// The v2 answer about one channel (NL-878): its <c>channel_announcement_2</c>, its <c>channel_update_2</c>s and
    /// its nodes' announcements (the <c>node_announcement_2</c>, else the <c>node_announcement</c>), as
    /// <paramref name="flag"/> asks (the BOLT 7 bits name the <c>_2</c> messages too).
    /// </summary>
    private void AddV2Replies(GraphChannel channel, ulong flag, List<IMessage> replies,
                              HashSet<CompactPubKey> sentNodes)
    {
        if ((flag & GossipQueryCodec.QueryFlagChannelAnnouncement) != 0)
            replies.Add(new ChannelAnnouncement2Message(ChannelAnnouncement2Payload.Parse(channel.RawAnnouncement2.Span)));
        if ((flag & GossipQueryCodec.QueryFlagChannelUpdate1) != 0 && ToMessage2(channel.Policy1V2) is { } update1)
            replies.Add(update1);
        if ((flag & GossipQueryCodec.QueryFlagChannelUpdate2) != 0 && ToMessage2(channel.Policy2V2) is { } update2)
            replies.Add(update2);
        if ((flag & GossipQueryCodec.QueryFlagNodeAnnouncement1) != 0)
            AddNode(channel.NodeId1, replies, sentNodes, preferV2: true);
        if ((flag & GossipQueryCodec.QueryFlagNodeAnnouncement2) != 0)
            AddNode(channel.NodeId2, replies, sentNodes, preferV2: true);
    }

    private static ChannelUpdate2Message? ToMessage2(GraphPolicy? policy) =>
        policy is null || policy.RawUpdate.IsEmpty
            ? null
            : new ChannelUpdate2Message(ChannelUpdate2Payload.Parse(policy.RawUpdate.Span));

    private static int LowerBound(GraphChannel[] sorted, ulong blockHeight)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (sorted[mid].ShortChannelId.BlockHeight < blockHeight)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static bool HasRawUpdate(GraphChannel channel) =>
        channel.Policy1 is { RawUpdate.IsEmpty: false } || channel.Policy2 is { RawUpdate.IsEmpty: false };

    private static ReplyChannelRangeMessage CreateReply(ChainHash chainHash, uint first, uint number,
                                                        bool syncComplete, IReadOnlyList<GraphChannel> channels,
                                                        bool withTimestamps, bool withChecksums,
                                                        bool includeV2 = false)
    {
        var encoded = GossipQueryCodec.EncodeShortChannelIds(channels.Select(c => c.ShortChannelId).ToList());
        BaseTlv? timestamps = null;
        BaseTlv? checksums = null;

        // NL-878: to a v2 requester, a channel listed by its v2 side carries block heights and v2 checksums
        if (withTimestamps && channels.Count > 0)
            timestamps = new BaseTlv(TlvConstants.ReplyChannelRangeTimestamps,
                                     GossipQueryCodec.EncodeTimestamps(
                                         channels.Select(c => includeV2 && IsListedAsV2(c)
                                                                  ? new ChannelUpdatePair(
                                                                      c.Policy1V2?.Timestamp ?? 0,
                                                                      c.Policy2V2?.Timestamp ?? 0)
                                                                  : new ChannelUpdatePair(
                                                                      c.Policy1?.Timestamp ?? 0,
                                                                      c.Policy2?.Timestamp ?? 0))
                                                 .ToList()));
        if (withChecksums && channels.Count > 0)
            checksums = new BaseTlv(TlvConstants.ReplyChannelRangeChecksums,
                                    GossipQueryCodec.EncodeChecksums(
                                        channels.Select(c => includeV2 && IsListedAsV2(c)
                                                                 ? new ChannelUpdatePair(Checksum2(c.Policy1V2),
                                                                                         Checksum2(c.Policy2V2))
                                                                 : new ChannelUpdatePair(Checksum(c.Policy1),
                                                                                         Checksum(c.Policy2)))
                                                .ToList()));

        return new ReplyChannelRangeMessage(
            new ReplyChannelRangePayload(chainHash, first, Math.Max(number, 1u), syncComplete, encoded), timestamps,
            checksums);
    }

    private static uint Checksum(GraphPolicy? policy) =>
        policy is null || policy.RawUpdate.IsEmpty ? 0 : ChannelUpdateChecksum.Compute(policy.RawUpdate.Span);

    /// <summary>
    /// The checksum of a <c>channel_update_2</c> (NL-878): CRC32C of its records without the signature and the block
    /// height (<see cref="ChannelUpdate2Payload.GetChecksumData"/>); 0 without one.
    /// </summary>
    internal static uint Checksum2(GraphPolicy? policy) =>
        policy is null || policy.RawUpdate.IsEmpty
            ? 0
            : ChannelUpdateChecksum.Crc32C(ChannelUpdate2Payload.Parse(policy.RawUpdate.Span).GetChecksumData());

    private static ChannelUpdateMessage? ToMessage(GraphPolicy? policy) =>
        policy is null || policy.RawUpdate.IsEmpty
            ? null
            : new ChannelUpdateMessage(ChannelUpdatePayload.Parse(policy.RawUpdate.Span));

    private void AddNode(CompactPubKey nodeId, List<IMessage> replies, HashSet<CompactPubKey> sentNodes,
                         bool preferV2 = false)
    {
        if (sentNodes.Contains(nodeId) || !_graphStore.TryGetNode(nodeId, out var node))
            return;

        // NL-878: to a v2 requester the node_announcement_2 when the node has one
        if (preferV2 && node.HasV2 && !node.RawAnnouncement2.IsEmpty)
        {
            sentNodes.Add(nodeId);
            replies.Add(new NodeAnnouncement2Message(NodeAnnouncement2Payload.Parse(node.RawAnnouncement2.Span)));
            return;
        }

        if (node.RawAnnouncement.IsEmpty)
            return;

        sentNodes.Add(nodeId);
        replies.Add(new NodeAnnouncementMessage(NodeAnnouncementPayload.Parse(node.RawAnnouncement.Span)));
    }

    private static ReplyShortChannelIdsEndMessage CreateEnd(ChainHash chainHash, bool fullInformation) =>
        new(new ReplyShortChannelIdsEndPayload(chainHash, fullInformation));

    internal static ulong ToUInt64(ShortChannelId shortChannelId) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(shortChannelId);
}