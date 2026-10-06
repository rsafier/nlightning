using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// BOLT 7 gossip query messages (261-265), checked against hand-built wire bytes that follow the spec field layout.
/// </summary>
public class GossipQueryMessageTests
{
    // BOLT 0 chain_hash of regtest (genesis block hash, little-endian as on the wire)
    private const string RegtestHex = "06226E46111A0B59CAAF126043EB5BBF28C34F3A5E332A1FC7B2B73CF188910F";

    // Two short_channel_ids: 700000x1x0 and 700001x2x1 (u24 block || u24 tx index || u16 output)
    private const string Scid1Hex = "0AAE60" + "000001" + "0000";
    private const string Scid2Hex = "0AAE61" + "000002" + "0001";

    private readonly MessageSerializer _messageSerializer;

    public GossipQueryMessageTests()
    {
        var messageTypeSerializerFactory =
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvStreamSerializer);
        _messageSerializer = new MessageSerializer(NullLogger<MessageSerializer>.Instance,
                                                   messageTypeSerializerFactory);
    }

    [Fact]
    public async Task Given_QueryChannelRangeBytes_When_DeserializeMessageAsync_Then_FieldsAreParsed()
    {
        // Arrange
        // type 263 || chain_hash || first_blocknum=100 || number_of_blocks=1000 || query_option (type 1, len 1, 0x03)
        var bytes = Convert.FromHexString("0107" + RegtestHex + "00000064" + "000003E8" + "010103");
        using var stream = new MemoryStream(bytes);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        var query = Assert.IsType<QueryChannelRangeMessage>(message);
        Assert.Equal(ChainConstants.Regtest, query.Payload.ChainHash);
        Assert.Equal(100u, query.Payload.FirstBlocknum);
        Assert.Equal(1000u, query.Payload.NumberOfBlocks);
        Assert.NotNull(query.QueryOptionTlv);
        Assert.Equal(new byte[] { 0x03 }, query.QueryOptionTlv.Value);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_QueryChannelRangeWithoutTlvs_When_DeserializeMessageAsync_Then_QueryOptionIsNull()
    {
        // Arrange
        var bytes = Convert.FromHexString("0107" + RegtestHex + "00000000" + "FFFFFFFF");
        using var stream = new MemoryStream(bytes);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        var query = Assert.IsType<QueryChannelRangeMessage>(message);
        Assert.Equal(0u, query.Payload.FirstBlocknum);
        Assert.Equal(uint.MaxValue, query.Payload.NumberOfBlocks);
        Assert.Null(query.QueryOptionTlv);
        Assert.Null(query.Extension);
    }

    [Fact]
    public async Task Given_QueryChannelRangeWithUnknownEvenTlv_When_DeserializeMessageAsync_Then_Throws()
    {
        // Arrange
        var bytes = Convert.FromHexString("0107" + RegtestHex + "00000064" + "000003E8" + "02012A");
        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _messageSerializer
                                                                   .DeserializeMessageAsync(stream));
    }

    [Fact]
    public async Task Given_QueryShortChannelIdsBytes_When_DeserializeMessageAsync_Then_FieldsAreParsed()
    {
        // Arrange
        // type 261 || chain_hash || len=17 || encoding 0 + 2 scids || query_flags (type 1, len 3: encoding 0, 1, 6)
        var bytes = Convert.FromHexString("0105" + RegtestHex + "0011" + "00" + Scid1Hex + Scid2Hex + "0103000106");
        using var stream = new MemoryStream(bytes);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        var query = Assert.IsType<QueryShortChannelIdsMessage>(message);
        Assert.Equal(ChainConstants.Regtest, query.Payload.ChainHash);
        Assert.Equal(Convert.FromHexString("00" + Scid1Hex + Scid2Hex), query.Payload.EncodedShortIds.ToArray());
        Assert.NotNull(query.QueryFlagsTlv);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x06 }, query.QueryFlagsTlv.Value);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_QueryShortChannelIdsWithTruncatedIds_When_DeserializeMessageAsync_Then_Throws()
    {
        // Arrange
        // len says 17 bytes but only 9 follow
        var bytes = Convert.FromHexString("0105" + RegtestHex + "0011" + "00" + Scid1Hex);
        using var stream = new MemoryStream(bytes);

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _messageSerializer.DeserializeMessageAsync(stream));
    }

    [Fact]
    public async Task Given_GossipTimestampFilterBytes_When_DeserializeMessageAsync_Then_FieldsAreParsed()
    {
        // Arrange
        // type 265 || chain_hash || first_timestamp=0xFFFFFFFF || timestamp_range=0 (the "send me nothing" filter)
        var bytes = Convert.FromHexString("0109" + RegtestHex + "FFFFFFFF" + "00000000");
        using var stream = new MemoryStream(bytes);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        var filter = Assert.IsType<GossipTimestampFilterMessage>(message);
        Assert.Equal(ChainConstants.Regtest, filter.Payload.ChainHash);
        Assert.Equal(uint.MaxValue, filter.Payload.FirstTimestamp);
        Assert.Equal(0u, filter.Payload.TimestampRange);
    }

    [Fact]
    public async Task Given_AFilterWithABlockHeightRange_When_SerializeAsync_Then_TheTlvFollowsTheV1Fields()
    {
        // Arrange: taproot gossip (NL-878) gossip_timestamp_filter TLV 2 block_height_range = u32 || tu32
        var message = new GossipTimestampFilterMessage(
            new GossipTimestampFilterPayload(ChainConstants.Regtest, 5, 6),
            new Domain.Gossip.Queries.GossipBlockHeightRange(800_000, 2_016));
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var parsed = Assert.IsType<GossipTimestampFilterMessage>(await _messageSerializer.DeserializeMessageAsync(stream));

        // Assert: type 2, length 6, first_block_height 800000 (0x000C3500), num_blocks 2016 (0x07E0, minimal)
        Assert.Equal(Convert.FromHexString("0109" + RegtestHex + "00000005" + "00000006" + "0206" + "000C3500" + "07E0"),
                     stream.ToArray());
        Assert.Equal(new Domain.Gossip.Queries.GossipBlockHeightRange(800_000, 2_016), parsed.BlockHeightRange);
        Assert.Equal(5u, parsed.Payload.FirstTimestamp);
    }

    [Fact]
    public async Task Given_AFilterWithoutABlockHeightRange_When_SerializeAsync_Then_TheBolt7BytesAreUnchanged()
    {
        // Arrange
        var message = new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 5, 6));
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(Convert.FromHexString("0109" + RegtestHex + "00000005" + "00000006"), stream.ToArray());
        Assert.Null(message.BlockHeightRange);
    }

    [Fact]
    public async Task Given_AFilterWithAnUnknownEvenTlv_When_DeserializeMessageAsync_Then_Throws()
    {
        // Arrange: type 4 is not known (BOLT 1: an unknown even type fails the message); odd 3 is ignored
        var unknownEven = Convert.FromHexString("0109" + RegtestHex + "00000005" + "00000006" + "040100");
        var unknownOdd = Convert.FromHexString("0109" + RegtestHex + "00000005" + "00000006" + "030100");
        using var evenStream = new MemoryStream(unknownEven);
        using var oddStream = new MemoryStream(unknownOdd);

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _messageSerializer.DeserializeMessageAsync(evenStream));
        var parsed = Assert.IsType<GossipTimestampFilterMessage>(
            await _messageSerializer.DeserializeMessageAsync(oddStream));
        Assert.Null(parsed.BlockHeightRange);
    }

    [Fact]
    public async Task Given_EmptyReplyChannelRange_When_SerializeAsync_Then_WritesSpecLayout()
    {
        // Arrange
        var message = new ReplyChannelRangeMessage(
            new ReplyChannelRangePayload(ChainConstants.Regtest, 100, 1000, true, new byte[] { 0x00 }));
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        // type 264 || chain_hash || first_blocknum || number_of_blocks || sync_complete=1 || len=1 || encoding 0
        Assert.Equal(Convert.FromHexString("0108" + RegtestHex + "00000064" + "000003E8" + "01" + "0001" + "00"),
                     stream.ToArray());
    }

    [Fact]
    public async Task Given_ReplyChannelRangeBytesWithTlvs_When_DeserializeMessageAsync_Then_FieldsAreParsed()
    {
        // Arrange
        // one scid, timestamps_tlv (type 1: encoding 0 + 2 x u32) and checksums_tlv (type 3: 2 x u32)
        var bytes = Convert.FromHexString("0108" + RegtestHex + "000AAE60" + "00000002" + "00" + "0009" + "00"
                                        + Scid1Hex + "0109" + "00" + "5F5E1000" + "5F5E1001" + "0308" + "01020304"
                                        + "05060708");
        using var stream = new MemoryStream(bytes);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        var reply = Assert.IsType<ReplyChannelRangeMessage>(message);
        Assert.Equal(700000u, reply.Payload.FirstBlocknum);
        Assert.Equal(2u, reply.Payload.NumberOfBlocks);
        Assert.False(reply.Payload.SyncComplete);
        Assert.Equal(Convert.FromHexString("00" + Scid1Hex), reply.Payload.EncodedShortIds.ToArray());
        Assert.NotNull(reply.TimestampsTlv);
        Assert.NotNull(reply.ChecksumsTlv);
        Assert.Equal(Convert.FromHexString("0102030405060708"), reply.ChecksumsTlv.Value);
    }

    [Fact]
    public async Task Given_ReplyShortChannelIdsEnd_When_SerializeAsync_Then_WritesSpecLayout()
    {
        // Arrange
        var message = new ReplyShortChannelIdsEndMessage(
            new ReplyShortChannelIdsEndPayload(ChainConstants.Regtest, false));
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        // type 262 || chain_hash || full_information=0
        Assert.Equal(Convert.FromHexString("0106" + RegtestHex + "00"), stream.ToArray());
    }

    [Fact]
    public async Task Given_QueryMessagesWithTlvs_When_RoundTripped_Then_BytesAreIdentical()
    {
        // Arrange
        var queryChannelRange = new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest,
                                                                     1, 2),
                                                             new BaseTlv(TlvConstants.QueryOption, [0x01]));
        var queryShortChannelIds =
            new QueryShortChannelIdsMessage(
                new QueryShortChannelIdsPayload(ChainConstants.Regtest, Convert.FromHexString("00" + Scid1Hex)),
                new BaseTlv(TlvConstants.QueryFlags, [0x00, 0x1F]));
        var filter = new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 5, 6));

        IMessage[] originals = [queryChannelRange, queryShortChannelIds, filter];

        foreach (var original in originals)
        {
            using var stream = new MemoryStream();
            await _messageSerializer.SerializeAsync(original, stream);
            var bytes = stream.ToArray();

            // Act
            stream.Position = 0;
            var parsed = await _messageSerializer.DeserializeMessageAsync(stream);
            Assert.NotNull(parsed);
            using var reserialized = new MemoryStream();
            await _messageSerializer.SerializeAsync(parsed, reserialized);

            // Assert
            Assert.Equal(original.GetType(), parsed.GetType());
            Assert.Equal(bytes, reserialized.ToArray());
        }
    }
}