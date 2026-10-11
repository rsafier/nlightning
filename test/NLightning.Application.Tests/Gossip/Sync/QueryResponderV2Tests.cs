using NLightning.Tests.Utils.Gossip;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Sync;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Graph;

/// <summary>
/// The BOLT 7 query changes of taproot gossip (BOLTs PR #1059, NL-878): <see cref="QueryResponder"/> lists and serves
/// channels announced with <c>channel_announcement_2</c> only to a requester that negotiated <c>option_gossip_v2</c>.
/// </summary>
public class QueryResponderV2Tests
{
    private static readonly ChainHash s_chain = ChainConstants.Regtest;
    private static readonly ShortChannelId s_v1Scid = new(100, 1, 0);
    private static readonly ShortChannelId s_v2Scid = new(200, 1, 0);
    private static readonly GossipV2TestKey s_carol = new(41);
    private static readonly GossipV2TestKey s_dave = new(42);

    /// <summary>A v1 channel at block 100 and a v2-only channel at block 200 with both updates and Carol's node.</summary>
    private static (SyncTestGraph Graph, ChannelAnnouncement2Payload Announcement, ChannelUpdate2Payload Update1,
        ChannelUpdate2Payload Update2, NodeAnnouncement2Payload Node) CreateGraph()
    {
        var graph = new SyncTestGraph();
        graph.AddSignedChannel(s_v1Scid, SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var announcement = GossipV2TestSigner.SignedChannelAnnouncement2(s_chain, s_v2Scid, 1_000_000, s_carol,
                                                                         s_dave, new GossipV2TestKey(51),
                                                                         new GossipV2TestKey(52),
                                                                         GraphTestKit.TxIdFor(s_v2Scid));
        var channel = new GraphChannel(s_v2Scid, announcement.NodeId1, announcement.NodeId2,
                                       announcement.BitcoinKey1, announcement.BitcoinKey2, 1_000_000)
        {
            Versions = GraphGossipVersions.V2,
            RawAnnouncement2 = announcement.GetBytes()
        };
        Assert.True(graph.Store.TryAddChannel(channel));
        var node1 = announcement.NodeId1 == s_carol.PubKey ? s_carol : s_dave;
        var node2 = node1 == s_carol ? s_dave : s_carol;
        var update1 = GossipV2TestSigner.SignedChannelUpdate2(s_chain, s_v2Scid, 0, 2_500, node1);
        var update2 = GossipV2TestSigner.SignedChannelUpdate2(s_chain, s_v2Scid, 1, 2_501, node2, feeBaseMsat: 7);
        foreach (var update in (ChannelUpdate2Payload[])[update1, update2])
            Assert.True(graph.Store.TryApplyPolicy(s_v2Scid, GraphPolicy.FromChannelUpdate2(update, 1_000_000_000)
                                                                with
            { RawUpdate = update.GetBytes() }));
        var node = GossipV2TestSigner.SignedNodeAnnouncement2(s_carol, 2_500);
        Assert.True(graph.Store.TryApplyNode2(GraphNode.FromNodeAnnouncement2(node, node.GetBytes())));
        return (graph, announcement, update1, update2, node);
    }

    private static QueryChannelRangeMessage Query(ulong option) =>
        new(new QueryChannelRangePayload(s_chain, 0, 1_000),
            new BaseTlv(TlvConstants.QueryOption, GossipQueryCodec.EncodeQueryOption(option)));

    private static ShortChannelId[] Scids(ReplyChannelRangeMessage reply) =>
        GossipQueryCodec.DecodeShortChannelIds(reply.Payload.EncodedShortIds.Span, "test");

    [Fact]
    public void Given_AV2OnlyChannel_When_ARequesterWithoutGossipV2QueriesTheRange_Then_ItIsLeftOut()
    {
        // Arrange
        var (graph, _, _, _, _) = CreateGraph();
        var responder = new QueryResponder(graph.Store);

        // Act
        var replies = responder.CreateRangeReplies(Query(0), s_chain);

        // Assert: the draft's "MUST only include short channel IDs for channels announced with channel_announcement"
        Assert.Equal([s_v1Scid], replies.SelectMany(Scids));
    }

    [Fact]
    public void Given_AV2OnlyChannel_When_AGossipV2RequesterQueriesTheRange_Then_ItIsListedWithBlockHeightsAndV2Checksums()
    {
        // Arrange
        var (graph, _, update1, update2, _) = CreateGraph();
        var responder = new QueryResponder(graph.Store);

        // Act
        var replies = responder.CreateRangeReplies(Query(GossipQueryCodec.QueryOptionTimestamps
                                                       | GossipQueryCodec.QueryOptionChecksums), s_chain,
                                                   includeV2: true);

        // Assert
        var reply = Assert.Single(replies);
        Assert.Equal([s_v1Scid, s_v2Scid], Scids(reply));
        var timestamps = GossipQueryCodec.DecodeTimestamps(reply.TimestampsTlv!.Value, 2);
        var checksums = GossipQueryCodec.DecodeChecksums(reply.ChecksumsTlv!.Value, 2);
        Assert.Equal(new ChannelUpdatePair(1_700_000_000, 1_700_000_001), timestamps[0]);
        Assert.Equal(new ChannelUpdatePair(2_500, 2_501), timestamps[1]);
        Assert.Equal(new ChannelUpdatePair(ChannelUpdateChecksum.Crc32C(update1.GetChecksumData()),
                                           ChannelUpdateChecksum.Crc32C(update2.GetChecksumData())), checksums[1]);
        Assert.NotEqual(checksums[1].Node1, checksums[1].Node2);
    }

    [Fact]
    public void Given_AV2OnlyChannel_When_AGossipV2RequesterQueriesItsScid_Then_TheV2MessagesAreServedByteExact()
    {
        // Arrange
        var (graph, announcement, update1, update2, node) = CreateGraph();
        var responder = new QueryResponder(graph.Store);
        var query = new QueryShortChannelIdsMessage(
            new QueryShortChannelIdsPayload(s_chain, GossipQueryCodec.EncodeShortChannelIds([s_v2Scid])));

        // Act
        var replies = responder.CreateShortChannelIdsReplies(query, s_chain, true, includeV2: true);

        // Assert: the announcement, both updates, the node_announcement_2 of Carol (Dave has none), the end
        Assert.Equal(5, replies.Count);
        Assert.Equal(announcement.GetBytes(), Assert.IsType<ChannelAnnouncement2Message>(replies[0]).Payload.GetBytes());
        Assert.Equal(update1.GetBytes(), Assert.IsType<ChannelUpdate2Message>(replies[1]).Payload.GetBytes());
        Assert.Equal(update2.GetBytes(), Assert.IsType<ChannelUpdate2Message>(replies[2]).Payload.GetBytes());
        Assert.Equal(node.GetBytes(), Assert.IsType<NodeAnnouncement2Message>(replies[3]).Payload.GetBytes());
        Assert.IsType<ReplyShortChannelIdsEndMessage>(replies[4]);
    }

    [Fact]
    public void Given_AV2OnlyChannel_When_ARequesterWithoutGossipV2QueriesItsScid_Then_OnlyTheEndIsSent()
    {
        // Arrange
        var (graph, _, _, _, _) = CreateGraph();
        var responder = new QueryResponder(graph.Store);
        var query = new QueryShortChannelIdsMessage(
            new QueryShortChannelIdsPayload(s_chain, GossipQueryCodec.EncodeShortChannelIds([s_v2Scid, s_v1Scid])));

        // Act
        var replies = responder.CreateShortChannelIdsReplies(query, s_chain, true);

        // Assert: the v1 channel's announcement, its two updates, the end; nothing of the v2 one
        Assert.DoesNotContain(replies, r => r is ChannelAnnouncement2Message or ChannelUpdate2Message
                                                                           or NodeAnnouncement2Message);
        Assert.IsType<ChannelAnnouncementMessage>(replies[0]);
        Assert.IsType<ReplyShortChannelIdsEndMessage>(replies[^1]);
    }
}