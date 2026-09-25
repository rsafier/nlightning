namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Exceptions;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Node.Services;

public class GossipQueryResponderTests
{
    private const string Scid1Hex = "0AAE60" + "000001" + "0000";
    private const string Scid2Hex = "0AAE61" + "000002" + "0001";

    private static QueryShortChannelIdsMessage CreateQuery(string encodedShortIdsHex, byte[]? queryFlags = null)
    {
        return new QueryShortChannelIdsMessage(
            new QueryShortChannelIdsPayload(ChainConstants.Regtest, Convert.FromHexString(encodedShortIdsHex)),
            queryFlags is null ? null : new BaseTlv(TlvConstants.QueryFlags, queryFlags));
    }

    [Fact]
    public void Given_QueryChannelRange_When_CreateReply_Then_ReplyCoversRangeAndIsFinal()
    {
        // Arrange
        var query = new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Main, 700000, 10000),
                                                 new BaseTlv(TlvConstants.QueryOption, [0x03]));

        // Act
        var reply = GossipQueryResponder.CreateReply(query);

        // Assert
        Assert.Equal(ChainConstants.Main, reply.Payload.ChainHash);
        Assert.Equal(700000u, reply.Payload.FirstBlocknum);
        Assert.Equal(10000u, reply.Payload.NumberOfBlocks);
        Assert.True(reply.Payload.SyncComplete);
        Assert.Equal(new byte[] { 0x00 }, reply.Payload.EncodedShortIds.ToArray());
        // query_option only lets us append timestamps/checksums (MAY); with no short_channel_ids there is nothing to
        // append
        Assert.Null(reply.Extension);
    }

    [Fact]
    public void Given_QueryChannelRangeWithZeroBlocks_When_CreateReply_Then_ReplyCoversAtLeastOneBlock()
    {
        // Arrange
        var query = new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest, 42, 0));

        // Act
        var reply = GossipQueryResponder.CreateReply(query);

        // Assert
        // BOLT 7: first_blocknum + number_of_blocks MUST be greater than the query's first_blocknum
        Assert.Equal(42u, reply.Payload.FirstBlocknum);
        Assert.Equal(1u, reply.Payload.NumberOfBlocks);
        Assert.True(reply.Payload.SyncComplete);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("00" + Scid1Hex)]
    [InlineData("00" + Scid1Hex + Scid2Hex)]
    public void Given_ValidQueryShortChannelIds_When_CreateReply_Then_ReplyEndHasNoFullInformation(string encodedHex)
    {
        // Arrange
        var query = CreateQuery(encodedHex);

        // Act
        var reply = GossipQueryResponder.CreateReply(query);

        // Assert
        Assert.Equal(ChainConstants.Regtest, reply.Payload.ChainHash);
        Assert.False(reply.Payload.FullInformation);
    }

    [Fact]
    public void Given_QueryShortChannelIdsWithOneFlagPerScid_When_CreateReply_Then_ReplyIsReturned()
    {
        // Arrange
        // encoding 0, flag 0x1f (one byte), flag 0xfd0100 (a 3-byte bigsize)
        var query = CreateQuery("00" + Scid1Hex + Scid2Hex, [0x00, 0x1F, 0xFD, 0x01, 0x00]);

        // Act
        var reply = GossipQueryResponder.CreateReply(query);

        // Assert
        Assert.False(reply.Payload.FullInformation);
    }

    [Theory]
    [InlineData("")] // no encoding byte
    [InlineData("01" + Scid1Hex)] // zlib encoding, which MUST NOT be used
    [InlineData("00" + Scid1Hex + "0000")] // not a whole number of short_channel_ids
    public void Given_MalformedEncodedShortIds_When_CreateReply_Then_ThrowsWarningException(string encodedHex)
    {
        // Arrange
        var query = CreateQuery(encodedHex);

        // Act & Assert
        Assert.Throws<WarningException>(() => GossipQueryResponder.CreateReply(query));
    }

    [Theory]
    [InlineData(new byte[] { })] // no encoding byte
    [InlineData(new byte[] { 0x01, 0x01, 0x01 })] // unknown encoding
    [InlineData(new byte[] { 0x00, 0x01 })] // one flag for two short_channel_ids
    [InlineData(new byte[] { 0x00, 0x01, 0x01, 0x01 })] // three flags for two short_channel_ids
    [InlineData(new byte[] { 0x00, 0x01, 0xFD, 0x01 })] // truncated bigsize
    public void Given_MalformedQueryFlags_When_CreateReply_Then_ThrowsWarningException(byte[] queryFlags)
    {
        // Arrange
        var query = CreateQuery("00" + Scid1Hex + Scid2Hex, queryFlags);

        // Act & Assert
        Assert.Throws<WarningException>(() => GossipQueryResponder.CreateReply(query));
    }
}