using NLightning.Tests.Utils.Vectors;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Sync;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Graph;
using Domain.Gossip.Queries;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// BOLT 7 plan G3-T1 (and the responder half of G3-T4): <see cref="QueryResponder"/> answers from the graph.
/// </summary>
public class QueryResponderTests
{
    private static readonly ChainHash s_chain = ChainConstants.Regtest;

    [Fact]
    public void Given_ManyChannels_When_ARangeIsQueried_Then_RepliesAreChunkedMonotonicAndCoverTheQuery()
    {
        // Arrange: 20 channels over heights 100..119, 3 per reply
        var graph = new SyncTestGraph();
        for (uint i = 0; i < 20; i++)
            graph.AddUnsignedChannel(new ShortChannelId(100 + i, 1, 0));
        var responder = CreateResponder(graph, maxPerReply: 3);

        // Act
        var replies = responder.CreateRangeReplies(Query(50, 200), s_chain);

        // Assert
        AssertValidRangeReplies(replies, 50, 200);
        Assert.Equal(7, replies.Count);
        Assert.All(replies, r => Assert.True(Scids(r).Length <= 3));
        Assert.Equal(Enumerable.Range(100, 20).Select(h => (uint)h),
                     replies.SelectMany(Scids).Select(s => s.BlockHeight));
    }

    [Fact]
    public void Given_ABlockWithMoreChannelsThanFitAReply_When_Queried_Then_ItIsSplitAndFirstBlocknumNeverDecreases()
    {
        // Arrange: 5 channels in block 300, 2 per reply
        var graph = new SyncTestGraph();
        for (uint i = 0; i < 5; i++)
            graph.AddUnsignedChannel(new ShortChannelId(300, i, 0));
        graph.AddUnsignedChannel(new ShortChannelId(301, 0, 0));
        var responder = CreateResponder(graph, maxPerReply: 2);

        // Act
        var replies = responder.CreateRangeReplies(Query(0, 1_000), s_chain);

        // Assert: BOLT 7 MAY split block contents; successive first_blocknum >= previous
        AssertValidRangeReplies(replies, 0, 1_000);
        Assert.Equal([0u, 300u, 300u], replies.Select(r => r.Payload.FirstBlocknum).ToArray());
        Assert.Equal(6, replies.SelectMany(Scids).Distinct().Count());
    }

    [Fact]
    public void Given_ALargeGraphWithTimestampsAndChecksums_When_Queried_Then_EveryReplyFitsOneMessage()
    {
        // Arrange: more channels than fit one 65,535-byte reply with both TLVs (24 bytes per channel)
        var graph = new SyncTestGraph();
        for (uint i = 0; i < 3_000; i++)
            graph.AddUnsignedChannel(new ShortChannelId(1_000 + i / 10, i % 10, 0));
        var responder = CreateResponder(graph);
        var query = Query(0, 10_000, GossipQueryCodec.QueryOptionTimestamps | GossipQueryCodec.QueryOptionChecksums);

        // Act
        var replies = responder.CreateRangeReplies(query, s_chain);

        // Assert
        AssertValidRangeReplies(replies, 0, 10_000);
        Assert.True(replies.Count >= 2);
        Assert.All(replies, r => Assert.True(WireLength(r) <= ushort.MaxValue, $"{WireLength(r)} bytes"));
        Assert.Equal(3_000, replies.SelectMany(Scids).Count());
    }

    [Fact]
    public void Given_TheDefaultLimit_When_8000ChannelsAreQueriedWithoutOptions_Then_OneReplyOf64001BytesOfIds()
    {
        // Arrange: plan §3.7, 8,000 ids per reply fit 65,535 bytes
        var graph = new SyncTestGraph();
        for (uint i = 0; i < 8_001; i++)
            graph.AddUnsignedChannel(new ShortChannelId(1_000 + i / 100, i % 100, 0));
        var responder = CreateResponder(graph);

        // Act
        var replies = responder.CreateRangeReplies(Query(0, 100_000), s_chain);

        // Assert
        Assert.Equal(2, replies.Count);
        Assert.Equal(1 + 8 * 8_000, replies[0].Payload.EncodedShortIds.Length);
        Assert.All(replies, r => Assert.True(WireLength(r) <= ushort.MaxValue));
        AssertValidRangeReplies(replies, 0, 100_000);
    }

    [Fact]
    public void Given_ChannelsWeDoNotServe_When_ARangeIsQueried_Then_OnlyServedChannelsInTheRangeAreListed()
    {
        // Arrange
        var graph = new SyncTestGraph();
        graph.AddSignedChannel(new ShortChannelId(99, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB); // before
        graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB); // served
        graph.AddSignedChannel(new ShortChannelId(101, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                               spentAtHeight: 150);
        graph.AddSignedChannel(new ShortChannelId(102, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB, null, null);
        graph.AddSignedChannel(new ShortChannelId(103, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                               verification: GraphChannelVerification.Unverified);
        graph.AddSignedChannel(new ShortChannelId(104, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                               timestamp1: null); // one update is enough
        graph.AddSignedChannel(new ShortChannelId(110, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB); // after
        var responder = CreateResponder(graph);

        // Act
        var replies = responder.CreateRangeReplies(Query(100, 10), s_chain);

        // Assert: spent, update-less and unverified channels are never offered
        Assert.Single(replies);
        Assert.Equal([new ShortChannelId(100, 0, 0), new ShortChannelId(104, 0, 0)], Scids(replies[0]));
    }

    [Fact]
    public void Given_QueryOption_When_ARangeIsQueried_Then_TimestampsAndChecksumsDescribeEachChannel()
    {
        // Arrange
        var graph = new SyncTestGraph();
        var full = graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                                          1_700_000_010, 1_700_000_020);
        var half = graph.AddSignedChannel(new ShortChannelId(101, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC,
                                          null, 1_700_000_030);
        var responder = CreateResponder(graph);

        // Act
        var both = responder.CreateRangeReplies(Query(0, 1_000, 3), s_chain).Single();
        var timestampsOnly = responder.CreateRangeReplies(Query(0, 1_000, 1), s_chain).Single();
        var none = responder.CreateRangeReplies(Query(0, 1_000), s_chain).Single();

        // Assert
        Assert.NotNull(both.TimestampsTlv);
        Assert.NotNull(both.ChecksumsTlv);
        Assert.Equal([new ChannelUpdatePair(1_700_000_010, 1_700_000_020), new ChannelUpdatePair(0, 1_700_000_030)],
                     GossipQueryCodec.DecodeTimestamps(both.TimestampsTlv.Value, 2));
        Assert.Equal([
                         new ChannelUpdatePair(ChannelUpdateChecksum.Compute(full.Policy1!.RawUpdate.Span),
                                               ChannelUpdateChecksum.Compute(full.Policy2!.RawUpdate.Span)),
                         new ChannelUpdatePair(0, ChannelUpdateChecksum.Compute(half.Policy2!.RawUpdate.Span))
                     ], GossipQueryCodec.DecodeChecksums(both.ChecksumsTlv.Value, 2));
        Assert.NotNull(timestampsOnly.TimestampsTlv);
        Assert.Null(timestampsOnly.ChecksumsTlv);
        Assert.Null(none.Extension);
    }

    [Fact]
    public void Given_ARangeQueryForAnotherChainOrOfZeroBlocks_When_Answered_Then_OneFinalReplyCoversIt()
    {
        // Arrange
        var graph = new SyncTestGraph();
        graph.AddUnsignedChannel(new ShortChannelId(42, 0, 0));
        var responder = CreateResponder(graph);

        // Act
        var otherChain = responder.CreateRangeReplies(
            new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Main, 0, 1_000)), s_chain);
        var zeroBlocks = responder.CreateRangeReplies(Query(42, 0), s_chain);

        // Assert
        Assert.Equal(ChainConstants.Main, Assert.Single(otherChain).Payload.ChainHash);
        Assert.Empty(Scids(otherChain[0]));
        AssertValidRangeReplies(zeroBlocks, 42, 1);
        Assert.Equal([new ShortChannelId(42, 0, 0)], Scids(zeroBlocks.Single()));
    }

    [Fact]
    public void Given_TheWholeU32Range_When_Queried_Then_TheFinalReplyEndsAtTheLargestBlocknum()
    {
        // Arrange: LND queries (0, 0xFFFFFFFF); first + number must fit a u32
        var graph = new SyncTestGraph();
        graph.AddUnsignedChannel(new ShortChannelId(500, 0, 0));
        var responder = CreateResponder(graph);

        // Act
        var reply = responder.CreateRangeReplies(Query(0, uint.MaxValue), s_chain).Single();

        // Assert
        Assert.Equal(0u, reply.Payload.FirstBlocknum);
        Assert.Equal(uint.MaxValue, reply.Payload.NumberOfBlocks);
        Assert.True(reply.Payload.SyncComplete);
    }

    [Fact]
    public void Given_NoQueryFlags_When_ChannelsAreQueried_Then_AnnouncementUpdatesAndNodesGoOutOncePerNode()
    {
        // Arrange: two channels sharing NodeA, an unknown scid in between
        var graph = new SyncTestGraph();
        var ab = graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var ac = graph.AddSignedChannel(new ShortChannelId(101, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeC);
        graph.AddNode(SyncTestGraph.NodeA);
        graph.AddNode(SyncTestGraph.NodeB);
        graph.AddNode(SyncTestGraph.NodeC);
        var responder = CreateResponder(graph);

        // Act
        var replies = responder.CreateShortChannelIdsReplies(
            ScidQuery([ab.ShortChannelId, new ShortChannelId(100, 5, 0), ac.ShortChannelId]), s_chain, true);

        // Assert: B7-Q-02, per channel 256, its latest updates, then its node announcements; no duplicate 257
        Assert.Equal([
                         MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
                         MessageTypes.NodeAnnouncement, MessageTypes.NodeAnnouncement,
                         MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.ChannelUpdate,
                         MessageTypes.NodeAnnouncement, MessageTypes.ReplyShortChannelIdsEnd
                     ], replies.Select(r => r.Type).ToArray());
        Assert.Equal(ab.RawAnnouncement.ToArray(),
                     ((ChannelAnnouncementMessage)replies[0]).Payload.GetBytes());
        Assert.Equal(ab.Policy1!.RawUpdate.ToArray(), ((ChannelUpdateMessage)replies[1]).Payload.GetBytes());
        Assert.Equal(3, replies.OfType<NodeAnnouncementMessage>().Select(n => n.Payload.NodeId).Distinct().Count());
        var end = (ReplyShortChannelIdsEndMessage)replies[^1];
        Assert.True(end.Payload.FullInformation);
        Assert.Equal(s_chain, end.Payload.ChainHash);
    }

    [Theory]
    [InlineData(GossipQueryCodec.QueryFlagChannelAnnouncement, new[] { MessageTypes.ChannelAnnouncement })]
    [InlineData(GossipQueryCodec.QueryFlagChannelUpdate1, new[] { MessageTypes.ChannelUpdate })]
    [InlineData(GossipQueryCodec.QueryFlagChannelUpdate2, new[] { MessageTypes.ChannelUpdate })]
    [InlineData(GossipQueryCodec.QueryFlagNodeAnnouncement1, new[] { MessageTypes.NodeAnnouncement })]
    [InlineData(GossipQueryCodec.QueryFlagNodeAnnouncement2, new[] { MessageTypes.NodeAnnouncement })]
    [InlineData(0UL, new MessageTypes[0])]
    [InlineData(0x20UL, new MessageTypes[0])] // an unknown bit asks for nothing
    public void Given_OneQueryFlag_When_AChannelIsQueried_Then_ExactlyWhatItAsksForIsSent(ulong flag,
        MessageTypes[] expected)
    {
        // Arrange
        var graph = new SyncTestGraph();
        var channel = graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA,
                                             SyncTestGraph.NodeB, 1_700_000_010, 1_700_000_020);
        graph.AddNode(SyncTestGraph.NodeA);
        graph.AddNode(SyncTestGraph.NodeB);
        var responder = CreateResponder(graph);

        // Act
        var replies = responder.CreateShortChannelIdsReplies(ScidQuery([channel.ShortChannelId], [flag]), s_chain,
                                                             false);

        // Assert
        Assert.Equal([.. expected, MessageTypes.ReplyShortChannelIdsEnd], replies.Select(r => r.Type).ToArray());
        if (flag == GossipQueryCodec.QueryFlagChannelUpdate2)
            Assert.Equal(1_700_000_020u, ((ChannelUpdateMessage)replies[0]).Payload.Timestamp);
        if (flag == GossipQueryCodec.QueryFlagNodeAnnouncement1)
            Assert.Equal(channel.NodeId1, ((NodeAnnouncementMessage)replies[0]).Payload.NodeId);
        if (flag == GossipQueryCodec.QueryFlagNodeAnnouncement2)
            Assert.Equal(channel.NodeId2, ((NodeAnnouncementMessage)replies[0]).Payload.NodeId);
        Assert.False(((ReplyShortChannelIdsEndMessage)replies[^1]).Payload.FullInformation);
    }

    [Fact]
    public void Given_SpentOrUnverifiedChannels_When_Queried_Then_TheyAreTreatedAsUnknown()
    {
        // Arrange
        var graph = new SyncTestGraph();
        var spent = graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB,
                                           spentAtHeight: 200);
        var unverified = graph.AddSignedChannel(new ShortChannelId(101, 0, 0), SyncTestGraph.NodeA,
                                                SyncTestGraph.NodeB,
                                                verification: GraphChannelVerification.Unverified);
        var responder = CreateResponder(graph);

        // Act
        var replies = responder.CreateShortChannelIdsReplies(
            ScidQuery([spent.ShortChannelId, unverified.ShortChannelId]), s_chain, true);

        // Assert
        Assert.Equal(MessageTypes.ReplyShortChannelIdsEnd, Assert.Single(replies).Type);
    }

    [Theory]
    [InlineData("01789c", null, "zlib")]
    [InlineData("02", null, "unknown")]
    [InlineData("0000006400000000", null, "whole number")]
    [InlineData("0000006400000000000000650000000000", "001f", "one flag per")]
    [InlineData("0000006400000000000000650000000000", "01011f", "zlib")]
    public void Given_AMalformedScidQuery_When_Answered_Then_AWarning(string encodedHex, string? flagsHex,
                                                                     string expected)
    {
        // Arrange
        var responder = CreateResponder(new SyncTestGraph());
        var query = new QueryShortChannelIdsMessage(
            new QueryShortChannelIdsPayload(s_chain, Convert.FromHexString(encodedHex)),
            flagsHex is null ? null : new BaseTlv(TlvConstants.QueryFlags, Convert.FromHexString(flagsHex)));

        // Act
        var exception = Assert.Throws<WarningException>(() => responder.CreateShortChannelIdsReplies(query, s_chain,
                                                                     true));

        // Assert
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Given_AScidQueryForAnotherChain_When_Answered_Then_OnlyTheEndWithoutFullInformation()
    {
        // Arrange
        var graph = new SyncTestGraph();
        var channel = graph.AddSignedChannel(new ShortChannelId(100, 0, 0), SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var responder = CreateResponder(graph);
        var query = new QueryShortChannelIdsMessage(new QueryShortChannelIdsPayload(
                                                        ChainConstants.Main,
                                                        GossipQueryCodec.EncodeShortChannelIds(
                                                            [channel.ShortChannelId])));

        // Act
        var reply = Assert.Single(responder.CreateShortChannelIdsReplies(query, s_chain, true));

        // Assert
        var end = Assert.IsType<ReplyShortChannelIdsEndMessage>(reply);
        Assert.False(end.Payload.FullInformation);
        Assert.Equal(ChainConstants.Main, end.Payload.ChainHash);
    }

    [Fact]
    public void Given_AMalformedQueryOption_When_ARangeIsQueried_Then_AWarning()
    {
        // Arrange
        var responder = CreateResponder(new SyncTestGraph());
        var query = new QueryChannelRangeMessage(new QueryChannelRangePayload(s_chain, 0, 10),
                                                 new BaseTlv(TlvConstants.QueryOption, [0xFD, 0x00, 0x01]));

        // Act / Assert
        Assert.Throws<WarningException>(() => responder.CreateRangeReplies(query, s_chain));
    }

    [Fact]
    public void Given_ClnsChannelAndUpdates_When_QueriedWithTimestampsAndChecksums_Then_OurReplyEqualsClns()
    {
        // Arrange (G3-T4 interop vector: the channel of CLN's captured reply_channel_range with CLN's two updates;
        // the announcement's signatures are not part of a range reply)
        var update0 = ChannelUpdatePayload.Parse(Bolt7QueryVectors.ClnUpdateDirection0.Payload);
        var update1 = ChannelUpdatePayload.Parse(Bolt7QueryVectors.ClnUpdateDirection1.Payload);
        var cln = Bolt7QueryVectors.ClnReplyChannelRange.Payload;
        var graph = new SyncTestGraph();
        var (node1, node2) = SyncTestGraph.Ordered(SyncTestGraph.NodeA, SyncTestGraph.NodeB);
        var announcement = new ChannelAnnouncementPayload(ChannelAnnouncementPayload.EmptySignature,
                                                          ChannelAnnouncementPayload.EmptySignature,
                                                          ChannelAnnouncementPayload.EmptySignature,
                                                          ChannelAnnouncementPayload.EmptySignature,
                                                          ReadOnlyMemory<byte>.Empty, s_chain, update0.ShortChannelId,
                                                          node1.PubKey, node2.PubKey, node1.PubKey, node2.PubKey);
        Assert.True(graph.Store.TryAddChannel(new GraphChannel(update0.ShortChannelId, node1.PubKey, node2.PubKey,
                                                               node1.PubKey, node2.PubKey, 1_000_000)
        {
            RawAnnouncement = announcement.GetBytes()
        }));
        foreach (var update in new[] { update0, update1 })
            Assert.True(graph.Store.TryApplyPolicy(update.ShortChannelId, GraphPolicy.FromChannelUpdate(update) with
            {
                RawUpdate = update.GetBytes()
            }));
        var responder = CreateResponder(graph);

        // Act: CLN's query was (0, 0x73) with query_option 3
        var reply = responder.CreateRangeReplies(Query(0, 0x73, 3), s_chain).Single();

        // Assert: every field and both TLV values equal CLN's reply
        Assert.Equal(cln[..32], ((byte[])reply.Payload.ChainHash));
        Assert.Equal(0u, reply.Payload.FirstBlocknum);
        Assert.Equal(0x73u, reply.Payload.NumberOfBlocks);
        Assert.True(reply.Payload.SyncComplete);
        Assert.Equal(cln[43..52], reply.Payload.EncodedShortIds.ToArray());
        Assert.Equal(cln[54..63], reply.TimestampsTlv!.Value);
        Assert.Equal(cln[65..73], reply.ChecksumsTlv!.Value);
    }

    internal static QueryResponder CreateResponder(SyncTestGraph graph, int maxPerReply = 8_000) =>
        new(graph.Store,
            Microsoft.Extensions.Options.Options.Create(new GossipSyncOptions { MaxScidsPerReply = maxPerReply }));

    private static QueryChannelRangeMessage Query(uint first, uint number, ulong? option = null) =>
        new(new QueryChannelRangePayload(s_chain, first, number),
            option is null ? null : new BaseTlv(TlvConstants.QueryOption, GossipQueryCodec.EncodeQueryOption(option.Value)));

    private static QueryShortChannelIdsMessage ScidQuery(ShortChannelId[] ids, ulong[]? flags = null) =>
        new(new QueryShortChannelIdsPayload(s_chain, GossipQueryCodec.EncodeShortChannelIds(ids)),
            flags is null ? null : new BaseTlv(TlvConstants.QueryFlags, GossipQueryCodec.EncodeQueryFlags(flags)));

    private static ShortChannelId[] Scids(ReplyChannelRangeMessage reply) =>
        GossipQueryCodec.DecodeShortChannelIds(reply.Payload.EncodedShortIds.Span, "test");

    /// <summary>
    /// The BOLT 7 rules a querier checks (B7-Q-04), through the sync side's own collector.
    /// </summary>
    private static void AssertValidRangeReplies(IReadOnlyList<ReplyChannelRangeMessage> replies, uint first,
                                                uint number)
    {
        var collector = new RangeReplyCollector(s_chain, first, Math.Max(number, 1));
        foreach (var reply in replies)
        {
            Assert.False(collector.IsComplete);
            collector.Add(reply);
        }

        Assert.True(collector.IsComplete);
        Assert.All(replies.SkipLast(1), r => Assert.False(r.Payload.SyncComplete));
        for (var i = 1; i < replies.Count; i++)
        {
            var previousEnd = (ulong)replies[i - 1].Payload.FirstBlocknum + replies[i - 1].Payload.NumberOfBlocks;
            Assert.True(replies[i].Payload.FirstBlocknum <= previousEnd, "a gap between replies");
        }
    }

    /// <summary>The BOLT 1 message length: type, fixed fields, encoded ids and the TLV records.</summary>
    private static int WireLength(IMessage message)
    {
        var reply = (ReplyChannelRangeMessage)message;
        var length = 2 + 32 + 4 + 4 + 1 + 2 + reply.Payload.EncodedShortIds.Length;
        foreach (var tlv in new[] { reply.TimestampsTlv, reply.ChecksumsTlv })
            if (tlv is not null)
                length += 1 + (tlv.Value.Length < 0xFD ? 1 : 3) + tlv.Value.Length;

        return length;
    }
}