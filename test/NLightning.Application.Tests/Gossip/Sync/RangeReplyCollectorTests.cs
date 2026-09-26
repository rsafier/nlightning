namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Sync;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Queries;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// B7-Q-04 as the querier checks it (plan G3-T2): <see cref="RangeReplyCollector"/>.
/// </summary>
public class RangeReplyCollectorTests
{
    private static readonly ChainHash s_chain = ChainConstants.Regtest;

    [Fact]
    public void Given_ValidReplies_When_Collected_Then_EveryChannelInTheQueryIsKeptInOrder()
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);

        // Act
        collector.Add(Reply(50, 80, false, new ShortChannelId(120, 0, 0), new ShortChannelId(101, 1, 0)));
        collector.Add(Reply(130, 20, false));
        collector.Add(Reply(130, 70, true, new ShortChannelId(150, 0, 1)));

        // Assert
        Assert.True(collector.IsComplete);
        Assert.Equal(3, collector.ReplyCount);
        Assert.Equal([new ShortChannelId(101, 1, 0), new ShortChannelId(120, 0, 0), new ShortChannelId(150, 0, 1)],
                     collector.Entries.Select(e => e.Key));
        Assert.All(collector.Entries, e => Assert.Null(e.Value));
    }

    [Theory]
    [InlineData(101u, 10u, "does not start")] // first_blocknum after the query's
    [InlineData(50u, 50u, "does not start")] // ends at the query's first block
    public void Given_ABadFirstReply_When_Added_Then_AWarning(uint first, uint number, string expected)
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);

        // Act
        var exception = Assert.Throws<WarningException>(() => collector.Add(Reply(first, number, false)));

        // Assert
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Given_ADecreasingFirstBlocknum_When_Added_Then_AWarning()
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);
        collector.Add(Reply(100, 50, false));

        // Act / Assert
        Assert.Contains("lower than the previous",
                        Assert.Throws<WarningException>(() => collector.Add(Reply(99, 200, true))).Message);
    }

    [Fact]
    public void Given_CoreLightningsWrappingReplies_When_Collected_Then_EveryChannelInTheQueryIsKept()
    {
        // Arrange: the headers noserver4u (CLN) sent on mainnet (NL-379): unsorted ids, number_of_blocks the u32
        // difference of the last and first id's blocks (wrapping), the next reply starting at the wrapped end
        var collector = new RangeReplyCollector(ChainConstants.Main, 0, 968_747);
        var mainnet = ChainConstants.Main;

        // Act
        collector.Add(Reply(mainnet, 0, 918_664, false, new ShortChannelId(930_994, 1, 0),
                            new ShortChannelId(833_604, 2, 1)));
        collector.Add(Reply(mainnet, 918_664, 4_294_956_303, false, new ShortChannelId(918_664, 3, 0),
                            new ShortChannelId(830_086, 4, 0)));
        collector.Add(Reply(mainnet, 907_671, 20_441, false, new ShortChannelId(907_671, 5, 0)));
        collector.Add(Reply(mainnet, 928_112, 15_650, false));
        collector.Add(Reply(mainnet, 943_762, 12_669, false));
        collector.Add(Reply(mainnet, 956_431, 4_294_959_446, false, new ShortChannelId(945_934, 6, 0)));
        Assert.False(collector.IsComplete);
        collector.Add(Reply(mainnet, 948_581, 18_264, false));
        collector.Add(Reply(mainnet, 966_845, 1_902, true, new ShortChannelId(968_739, 7, 0)));

        // Assert: complete, every id kept (also those outside their own reply's claimed blocks)
        Assert.True(collector.IsComplete);
        Assert.Equal(8, collector.ReplyCount);
        Assert.Equal(7, collector.Entries.Count);
    }

    [Fact]
    public void Given_ALowerFirstBlocknumThatDoesNotContinueThePreviousReply_When_Added_Then_AWarning()
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 0, 1_000);
        collector.Add(Reply(0, 100, false));
        collector.Add(Reply(100, 50, false));

        // Act / Assert: 60 is below 100 and is not the previous reply's end (150) or the block before it
        Assert.Contains("does not continue",
                        Assert.Throws<WarningException>(() => collector.Add(Reply(60, 940, true))).Message);
    }

    [Fact]
    public void Given_AFinalReplyShortOfTheQueryEnd_When_Added_Then_AWarning()
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);

        // Act / Assert: the final reply MUST reach first + number of the query
        Assert.Contains("before the queried end",
                        Assert.Throws<WarningException>(() => collector.Add(Reply(100, 99, true))).Message);
    }

    [Fact]
    public void Given_TheWholeU32Range_When_TheFinalReplyEndsAtTheLargestBlocknum_Then_ItIsComplete()
    {
        // Arrange: LND's cap, first + number fits a u32
        var collector = new RangeReplyCollector(s_chain, 0, uint.MaxValue);

        // Act
        collector.Add(Reply(0, uint.MaxValue, true));

        // Assert
        Assert.True(collector.IsComplete);
    }

    [Fact]
    public void Given_ACoveringReplyWithoutSyncComplete_When_Added_Then_MoreAreExpected()
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);

        // Act
        collector.Add(Reply(100, 100, false));

        // Assert
        Assert.False(collector.IsComplete);
    }

    [Theory]
    [InlineData("other chain")]
    [InlineData("zlib")]
    [InlineData("timestamps")]
    [InlineData("checksums")]
    [InlineData("after final")]
    public void Given_AMalformedReply_When_Added_Then_AWarning(string problem)
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);
        var scid = GossipQueryCodec.EncodeShortChannelIds([new ShortChannelId(120, 0, 0)]);
        var reply = problem switch
        {
            "other chain" => new ReplyChannelRangeMessage(
                new ReplyChannelRangePayload(ChainConstants.Main, 100, 100, true, scid)),
            "zlib" => new ReplyChannelRangeMessage(
                new ReplyChannelRangePayload(s_chain, 100, 100, true, new byte[] { 1, 0x78, 0x9c })),
            "timestamps" => new ReplyChannelRangeMessage(
                new ReplyChannelRangePayload(s_chain, 100, 100, true, scid),
                new BaseTlv(TlvConstants.ReplyChannelRangeTimestamps, new byte[] { 0, 1, 2, 3 })),
            "checksums" => new ReplyChannelRangeMessage(
                new ReplyChannelRangePayload(s_chain, 100, 100, true, scid), null,
                new BaseTlv(TlvConstants.ReplyChannelRangeChecksums, new byte[] { 0, 1, 2, 3 })),
            _ => Reply(100, 100, true)
        };
        if (problem == "after final")
            collector.Add(Reply(100, 100, true));

        // Act / Assert
        Assert.Throws<WarningException>(() => collector.Add(reply));
    }

    [Fact]
    public void Given_TimestampsAndIdsOutsideTheirBlocks_When_Added_Then_TimestampsAreKeptAndStrayIdsDropped()
    {
        // Arrange
        var collector = new RangeReplyCollector(s_chain, 100, 100);
        var ids = new[] { new ShortChannelId(99, 0, 0), new ShortChannelId(120, 0, 0), new ShortChannelId(250, 0, 0) };
        var reply = new ReplyChannelRangeMessage(
            new ReplyChannelRangePayload(s_chain, 90, 110, true, GossipQueryCodec.EncodeShortChannelIds(ids)),
            new BaseTlv(TlvConstants.ReplyChannelRangeTimestamps,
                        GossipQueryCodec.EncodeTimestamps([new(1, 2), new(3, 4), new(5, 6)])));

        // Act
        collector.Add(reply);

        // Assert: block 99 is before the query, block 250 after it
        var entry = Assert.Single(collector.Entries);
        Assert.Equal(new ShortChannelId(120, 0, 0), entry.Key);
        Assert.Equal(new ChannelUpdatePair(3, 4), entry.Value);
    }

    internal static ReplyChannelRangeMessage Reply(uint first, uint number, bool complete,
                                                   params ShortChannelId[] ids) =>
        Reply(s_chain, first, number, complete, ids);

    private static ReplyChannelRangeMessage Reply(ChainHash chain, uint first, uint number, bool complete,
                                                  params ShortChannelId[] ids) =>
        new(new ReplyChannelRangePayload(chain, first, number, complete,
                                         GossipQueryCodec.EncodeShortChannelIds(ids)));
}