namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Serialization.Interfaces;
using Exceptions;
using Helpers;
using Serialization.Messages.Types;

/// <summary>
/// BOLT 1: a message TLV extension with an unknown even type MUST fail, an unknown odd type MUST be ignored.
/// </summary>
public class MessageExtensionStrictnessTests
{
    private const string Point = "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";
    private const string Zero32 = "0000000000000000000000000000000000000000000000000000000000000000";

    // channel_type (type 1) carrying option_static_remotekey (bit 12)
    private const string ChannelTypeTlvHex = "01021000";

    private const string UnknownEvenTlvHex = "CA012A";
    private const string UnknownOddTlvHex = "C9012A";

    public static TheoryData<string> MessageNames =>
    [
        "init", "open_channel", "accept_channel", "open_channel2", "accept_channel2", "tx_init_rbf", "tx_ack_rbf",
        "channel_ready", "channel_reestablish", "closing_signed", "closing_signed_no_fee_range", "commitment_signed"
    ];

    [Theory]
    [MemberData(nameof(MessageNames))]
    public async Task Given_ExtensionWithUnknownEvenType_When_DeserializeAsync_Then_ThrowsMessageSerializationException(
        string messageName)
    {
        // Arrange
        var (serializer, baseHex) = GetCase(messageName);
        var stream = new MemoryStream(Convert.FromHexString(baseHex + UnknownEvenTlvHex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => serializer.DeserializeAsync(stream));
    }

    [Theory]
    [MemberData(nameof(MessageNames))]
    public async Task Given_ExtensionWithUnknownOddType_When_DeserializeAsync_Then_ReturnsMessage(string messageName)
    {
        // Arrange
        var (serializer, baseHex) = GetCase(messageName);
        var stream = new MemoryStream(Convert.FromHexString(baseHex + UnknownOddTlvHex));

        // Act
        var message = await serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Equal(stream.Length, stream.Position);
    }

    /// <summary>
    /// Returns the serializer and a valid message body (payload plus any required TLVs, all with types below 0xc9).
    /// </summary>
    private static (IMessageTypeSerializer Serializer, string BaseHex) GetCase(string messageName)
    {
        var payloadFactory = SerializerHelper.PayloadSerializerFactory;
        var tlvConverterFactory = SerializerHelper.TlvConverterFactory;
        var tlvStreamSerializer = SerializerHelper.TlvStreamSerializer;
        var points6 = string.Concat(Enumerable.Repeat(Point, 6));
        var points7 = string.Concat(Enumerable.Repeat(Point, 7));

        return messageName switch
        {
            "init" => (new InitMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                       "00000000"),
            "open_channel" => (
                new OpenChannel1MessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // chain_hash, temporary_channel_id, 6 x u64, u32, 2 x u16, 6 points, channel_flags
                Zero32 + Zero32 + new string('0', 6 * 16) + "000003E8" + "0090" + "01E3" + points6 + "00"
              + ChannelTypeTlvHex),
            "accept_channel" => (
                new AcceptChannel1MessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // temporary_channel_id, 4 x u64, u32, 2 x u16, 6 points
                Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + points6 + ChannelTypeTlvHex),
            "open_channel2" => (
                new OpenChannel2MessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // chain_hash, temporary_channel_id, 2 x u32, 4 x u64, 2 x u16, u32, 7 points, channel_flags
                Zero32 + Zero32 + "000003E8" + "000007D0" + new string('0', 4 * 16) + "0090" + "01E3" + "00000000"
              + points7 + "00"),
            "accept_channel2" => (
                new AcceptChannel2MessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // temporary_channel_id, 4 x u64, u32, 2 x u16, 6 points
                Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + points6),
            "tx_init_rbf" => (
                new TxInitRbfMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                Zero32 + "00000001" + "00000001"),
            "tx_ack_rbf" => (
                new TxAckRbfMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer), Zero32),
            "channel_ready" => (
                new ChannelReadyMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                Zero32 + Point),
            "channel_reestablish" => (
                new ChannelReestablishMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                Zero32 + "0000000000000001" + "0000000000000002" + Zero32 + Point),
            "closing_signed" => (
                new ClosingSignedMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // channel_id, fee_satoshis, signature, fee_range
                Zero32 + "0000000000000002"
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"
              + "011000000000000000010000000000000003"),
            "closing_signed_no_fee_range" => (
                new ClosingSignedMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // channel_id, fee_satoshis, signature
                Zero32 + "0000000000000002"
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"),
            "commitment_signed" => (
                new CommitmentSignedMessageTypeSerializer(payloadFactory, tlvConverterFactory, tlvStreamSerializer),
                // channel_id, signature, num_htlcs = 0, funding_txid
                Zero32
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"
              + "0000" + "0120" + Zero32),
            _ => throw new ArgumentOutOfRangeException(nameof(messageName), messageName, null)
        };
    }
}