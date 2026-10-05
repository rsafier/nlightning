using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// BOLT 2 "Channel Splicing" wire (SP1-A-T1): <c>splice_init</c> (80), <c>splice_ack</c> (81) and
/// <c>splice_locked</c> (77), byte-exact against hand-built encodings of the spec layouts.
/// </summary>
public class SpliceMessagesTests
{
    private const string ChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string PointHex = "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";
    private const string TxIdHex = "A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF";

    private const string UnknownEvenTlvHex = "CA012A";
    private const string UnknownOddTlvHex = "C9012A";

    private static readonly ChannelId s_channelId = new(Convert.FromHexString(ChannelIdHex));
    private static readonly CompactPubKey s_point = new(Convert.FromHexString(PointHex));

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer));

    private readonly IMessageTypeSerializer<SpliceInitMessage> _spliceInitSerializer =
        SerializerHelper.MessageTypeSerializerFactory.GetSerializer<SpliceInitMessage>()!;

    private readonly IMessageTypeSerializer<SpliceAckMessage> _spliceAckSerializer =
        SerializerHelper.MessageTypeSerializerFactory.GetSerializer<SpliceAckMessage>()!;

    private readonly IMessageTypeSerializer<SpliceLockedMessage> _spliceLockedSerializer =
        SerializerHelper.MessageTypeSerializerFactory.GetSerializer<SpliceLockedMessage>()!;

    #region splice_init

    [Theory]
    // splice-out of 50,000 sat: s64 two's complement
    [InlineData(-50_000L, "FFFFFFFFFFFF3CB0", false)]
    // splice-in of 100,000 sat with require_confirmed_inputs
    [InlineData(100_000L, "00000000000186A0", true)]
    [InlineData(0L, "0000000000000000", false)]
    [InlineData(long.MinValue, "8000000000000000", true)]
    public async Task Given_SpliceInit_When_SerializedAndDeserialized_Then_ItIsByteExactAndRoundTrips(
        long contribution, string contributionHex, bool requireConfirmedInputs)
    {
        // Arrange: channel_id || s64 funding_contribution_satoshis || u32 funding_feerate_perkw || u32 locktime ||
        // point funding_pubkey || splice_init_tlvs (type 2 require_confirmed_inputs)
        var message = new SpliceInitMessage(new SpliceInitPayload(s_channelId, contribution, 2_500, 800_123, s_point),
                                            requireConfirmedInputs ? new RequireConfirmedInputsTlv() : null);
        var expectedHex = "0050" + ChannelIdHex + contributionHex + "000009C4" + "000C357B" + PointHex
                        + (requireConfirmedInputs ? "0200" : "");

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync<SpliceInitMessage>(bytes);

        // Assert
        Assert.Equal(expectedHex, Convert.ToHexString(bytes));
        Assert.Equal(MessageTypes.SpliceInit, parsed.Type);
        Assert.Equal(s_channelId, parsed.Payload.ChannelId);
        Assert.Equal(contribution, parsed.Payload.FundingContributionSatoshis);
        Assert.Equal(2_500U, parsed.Payload.FundingFeeratePerKw);
        Assert.Equal(800_123U, parsed.Payload.Locktime);
        Assert.Equal(s_point, parsed.Payload.FundingPubKey);
        Assert.Equal(requireConfirmedInputs, parsed.RequireConfirmedInputsTlv is not null);
    }

    [Fact]
    public async Task Given_SpliceInitWithUnknownEvenTlv_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange (BOLT 1: an unknown even TLV type MUST fail the message)
        var stream = new MemoryStream(Convert.FromHexString(
                                          ChannelIdHex + "FFFFFFFFFFFF3CB0" + "000009C4" + "00000000" + PointHex
                                        + UnknownEvenTlvHex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _spliceInitSerializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_SpliceInitWithUnknownOddTlv_When_DeserializeAsync_Then_ItIsIgnored()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(
                                          ChannelIdHex + "FFFFFFFFFFFF3CB0" + "000009C4" + "00000000" + PointHex
                                        + "0200" + UnknownOddTlvHex));

        // Act
        var message = await _spliceInitSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(-50_000L, message.Payload.FundingContributionSatoshis);
        Assert.NotNull(message.RequireConfirmedInputsTlv);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_TruncatedSpliceInit_When_DeserializeAsync_Then_Throws()
    {
        // Arrange (the funding_pubkey is cut short)
        var stream = new MemoryStream(Convert.FromHexString(
                                          ChannelIdHex + "0000000000000001" + "000009C4" + "00000000"
                                        + PointHex[..40]));

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _spliceInitSerializer.DeserializeAsync(stream));
    }

    #endregion

    #region splice_ack

    [Theory]
    [InlineData(0L, "0000000000000000", false)]
    [InlineData(-1L, "FFFFFFFFFFFFFFFF", true)]
    [InlineData(21_000_000L, "0000000001406F40", false)]
    public async Task Given_SpliceAck_When_SerializedAndDeserialized_Then_ItIsByteExactAndRoundTrips(
        long contribution, string contributionHex, bool requireConfirmedInputs)
    {
        // Arrange: channel_id || s64 funding_contribution_satoshis || point funding_pubkey || splice_ack_tlvs
        var message = new SpliceAckMessage(new SpliceAckPayload(s_channelId, contribution, s_point),
                                           requireConfirmedInputs ? new RequireConfirmedInputsTlv() : null);
        var expectedHex = "0051" + ChannelIdHex + contributionHex + PointHex + (requireConfirmedInputs ? "0200" : "");

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync<SpliceAckMessage>(bytes);

        // Assert
        Assert.Equal(expectedHex, Convert.ToHexString(bytes));
        Assert.Equal(MessageTypes.SpliceAck, parsed.Type);
        Assert.Equal(s_channelId, parsed.Payload.ChannelId);
        Assert.Equal(contribution, parsed.Payload.FundingContributionSatoshis);
        Assert.Equal(s_point, parsed.Payload.FundingPubKey);
        Assert.Equal(requireConfirmedInputs, parsed.RequireConfirmedInputsTlv is not null);
    }

    [Fact]
    public async Task Given_SpliceAckWithUnknownEvenTlv_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0000000000000000" + PointHex
                                                          + UnknownEvenTlvHex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _spliceAckSerializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_SpliceAckWithUnknownOddTlv_When_DeserializeAsync_Then_ItIsIgnored()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0000000000000000" + PointHex
                                                          + UnknownOddTlvHex));

        // Act
        var message = await _spliceAckSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.RequireConfirmedInputsTlv);
        Assert.Equal(stream.Length, stream.Position);
    }

    #endregion

    #region splice_locked

    [Fact]
    public async Task Given_SpliceLocked_When_SerializedAndDeserialized_Then_ItIsByteExactAndRoundTrips()
    {
        // Arrange: channel_id || sha256 splice_txid
        var txId = new TxId(Convert.FromHexString(TxIdHex));
        var message = new SpliceLockedMessage(new SpliceLockedPayload(s_channelId, txId));

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync<SpliceLockedMessage>(bytes);

        // Assert
        Assert.Equal("004D" + ChannelIdHex + TxIdHex, Convert.ToHexString(bytes));
        Assert.Equal(MessageTypes.SpliceLocked, parsed.Type);
        Assert.Equal(s_channelId, parsed.Payload.ChannelId);
        Assert.Equal(txId, parsed.Payload.SpliceTxId);
    }

    [Fact]
    public async Task Given_SpliceLockedWithUnknownEvenTlv_When_DeserializeAsync_Then_Throws()
    {
        // Arrange (splice_locked defines no TLVs: an even extension record fails the message, BOLT 1)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + TxIdHex + UnknownEvenTlvHex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() =>
                                                                   _spliceLockedSerializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_SpliceLockedWithUnknownOddTlv_When_DeserializeAsync_Then_ItIsIgnored()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + TxIdHex + UnknownOddTlvHex));

        // Act
        var message = await _spliceLockedSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(TxIdHex, Convert.ToHexString(message.Payload.SpliceTxId));
        Assert.Equal(stream.Length, stream.Position);
    }

    #endregion

    #region tx_init_rbf / tx_ack_rbf funding_output_contribution (s64)

    [Fact]
    public async Task Given_TxInitRbfWithNegativeContribution_When_RoundTripped_Then_S64IsKept()
    {
        // Arrange (BOLT 2 tx_init_rbf_tlvs type 0 funding_output_contribution is [s64:satoshis]: a splice-out RBF
        // carries a negative value)
        var message = new TxInitRbfMessage(new TxInitRbfPayload(s_channelId, 2_600, 0),
                                           new FundingOutputContributionTlv(-50_000L));

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync<TxInitRbfMessage>(bytes);

        // Assert
        Assert.Equal("0048" + ChannelIdHex + "00000000" + "00000A28" + "0008FFFFFFFFFFFF3CB0",
                     Convert.ToHexString(bytes));
        Assert.NotNull(parsed.FundingOutputContributionTlv);
        Assert.Equal(-50_000L, parsed.FundingOutputContributionTlv.Satoshis);
    }

    [Fact]
    public async Task Given_TxAckRbfWithNegativeContribution_When_RoundTripped_Then_S64IsKept()
    {
        // Arrange (tx_ack_rbf_tlvs type 0 is [s64:satoshis] too)
        var message = new TxAckRbfMessage(new TxAckRbfPayload(s_channelId),
                                          new FundingOutputContributionTlv(-1L), new RequireConfirmedInputsTlv());

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync<TxAckRbfMessage>(bytes);

        // Assert
        Assert.Equal("0049" + ChannelIdHex + "0008FFFFFFFFFFFFFFFF" + "0200", Convert.ToHexString(bytes));
        Assert.NotNull(parsed.FundingOutputContributionTlv);
        Assert.Equal(-1L, parsed.FundingOutputContributionTlv.Satoshis);
        Assert.NotNull(parsed.RequireConfirmedInputsTlv);
    }

    [Fact]
    public async Task Given_TxInitRbfWithDistinctLocktimeAndFeerate_When_Deserialized_Then_TheyAreNotSwapped()
    {
        // Arrange (regression: the payload serializer passed locktime and feerate to the (channel_id, feerate,
        // locktime) constructor in the wrong order; the wire order is locktime then feerate)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "000C357B" + "00000A28"));
        var serializer = SerializerHelper.MessageTypeSerializerFactory.GetSerializer<TxInitRbfMessage>()!;

        // Act
        var message = await serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(800_123U, message.Payload.Locktime);
        Assert.Equal(2_600U, message.Payload.Feerate);
        Assert.Null(message.FundingOutputContributionTlv);
    }

    [Fact]
    public async Task Given_TxInitRbfWithPositiveContribution_When_Deserialized_Then_ItIsWholeSatoshis()
    {
        // Arrange (regression: the contribution is satoshis on the wire, 10 means 10 sat)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "00000001" + "00000001"
                                                          + "0008000000000000000A"));
        var serializer = SerializerHelper.MessageTypeSerializerFactory.GetSerializer<TxInitRbfMessage>()!;

        // Act
        var message = await serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(10L, message.FundingOutputContributionTlv!.Satoshis);
    }

    #endregion

    private async Task<byte[]> SerializeAsync(IMessage message)
    {
        using var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private async Task<TMessage> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : class, IMessage
    {
        using var stream = new MemoryStream(bytes);
        var message = await _messageSerializer.DeserializeMessageAsync(stream);
        return Assert.IsType<TMessage>(message);
    }
}