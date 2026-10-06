using NLightning.Infrastructure.Serialization.Wire;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Exceptions;
using Helpers;

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
        "channel_ready", "channel_reestablish", "closing_signed", "closing_signed_no_fee_range", "commitment_signed",
        "tx_add_input", "tx_add_input_shared", "tx_signatures", "tx_signatures_shared", "funding_created",
        "funding_signed", "revoke_and_ack", "shutdown", "tx_complete"
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
        var points6 = string.Concat(Enumerable.Repeat(Point, 6));
        var points7 = string.Concat(Enumerable.Repeat(Point, 7));

        return messageName switch
        {
            "init" => (SerializerHelper.WireRegistry.Get<InitMessage>()!,
                       "00000000"),
            "open_channel" => (
                SerializerHelper.WireRegistry.Get<OpenChannel1Message>()!,
                // chain_hash, temporary_channel_id, 6 x u64, u32, 2 x u16, 6 points, channel_flags
                Zero32 + Zero32 + new string('0', 6 * 16) + "000003E8" + "0090" + "01E3" + points6 + "00"
              + ChannelTypeTlvHex),
            "accept_channel" => (
                SerializerHelper.WireRegistry.Get<AcceptChannel1Message>()!,
                // temporary_channel_id, 4 x u64, u32, 2 x u16, 6 points
                Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + points6 + ChannelTypeTlvHex),
            "open_channel2" => (
                SerializerHelper.WireRegistry.Get<OpenChannel2Message>()!,
                // chain_hash, temporary_channel_id, 2 x u32, 4 x u64, 2 x u16, u32, 7 points, channel_flags
                Zero32 + Zero32 + "000003E8" + "000007D0" + new string('0', 4 * 16) + "0090" + "01E3" + "00000000"
              + points7 + "00"),
            "accept_channel2" => (
                SerializerHelper.WireRegistry.Get<AcceptChannel2Message>()!,
                // temporary_channel_id, 4 x u64, u32, 2 x u16, 7 points (BOLT 2: second_per_commitment_point too)
                Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + points7),
            "tx_init_rbf" => (
                SerializerHelper.WireRegistry.Get<TxInitRbfMessage>()!,
                Zero32 + "00000001" + "00000001"),
            "tx_ack_rbf" => (
                SerializerHelper.WireRegistry.Get<TxAckRbfMessage>()!, Zero32),
            "channel_ready" => (
                SerializerHelper.WireRegistry.Get<ChannelReadyMessage>()!,
                Zero32 + Point),
            "channel_reestablish" => (
                SerializerHelper.WireRegistry.Get<ChannelReestablishMessage>()!,
                Zero32 + "0000000000000001" + "0000000000000002" + Zero32 + Point),
            "closing_signed" => (
                SerializerHelper.WireRegistry.Get<ClosingSignedMessage>()!,
                // channel_id, fee_satoshis, signature, fee_range
                Zero32 + "0000000000000002"
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"
              + "011000000000000000010000000000000003"),
            "closing_signed_no_fee_range" => (
                SerializerHelper.WireRegistry.Get<ClosingSignedMessage>()!,
                // channel_id, fee_satoshis, signature
                Zero32 + "0000000000000002"
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"),
            "commitment_signed" => (
                SerializerHelper.WireRegistry.Get<CommitmentSignedMessage>()!,
                // channel_id, signature, num_htlcs = 0, funding_txid
                Zero32
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"
              + "0000" + "0120" + Zero32),
            "tx_add_input" => (
                SerializerHelper.WireRegistry.Get<TxAddInputMessage>()!,
                // channel_id, serial_id, prevtx_len = 4, prevtx, prevtx_vout, sequence
                Zero32 + "0000000000000002" + "0004" + "00010203" + "00000000" + "FFFFFFFD"),
            "tx_add_input_shared" => (
                SerializerHelper.WireRegistry.Get<TxAddInputMessage>()!,
                // channel_id, serial_id, prevtx_len = 0, prevtx_vout, sequence, shared_input_txid
                Zero32 + "0000000000000002" + "0000" + "00000000" + "FFFFFFFD" + "0020" + Zero32),
            "tx_signatures" => (
                SerializerHelper.WireRegistry.Get<TxSignaturesMessage>()!,
                // channel_id, txid, num_witnesses = 0
                Zero32 + Zero32 + "0000"),
            "tx_signatures_shared" => (
                SerializerHelper.WireRegistry.Get<TxSignaturesMessage>()!,
                // channel_id, txid, num_witnesses = 0, shared_input_signature
                Zero32 + Zero32 + "0000" + "0040"
              + "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320"),
            // Read strictly since the simple taproot TLVs (NL-877); their extension was ignored before
            "funding_created" => (
                SerializerHelper.WireRegistry.Get<FundingCreatedMessage>()!,
                // temporary_channel_id, funding_txid, funding_output_index, signature
                Zero32 + Zero32 + "0000" + new string('0', 128)),
            "funding_signed" => (
                SerializerHelper.WireRegistry.Get<FundingSignedMessage>()!,
                // channel_id, signature
                Zero32 + new string('0', 128)),
            "revoke_and_ack" => (
                SerializerHelper.WireRegistry.Get<RevokeAndAckMessage>()!,
                // channel_id, per_commitment_secret, next_per_commitment_point
                Zero32 + Zero32 + Point),
            "shutdown" => (
                SerializerHelper.WireRegistry.Get<ShutdownMessage>()!,
                // channel_id, len = 22, scriptpubkey (P2WPKH)
                Zero32 + "0016" + "0014" + new string('0', 40)),
            "tx_complete" => (
                SerializerHelper.WireRegistry.Get<TxCompleteMessage>()!,
                Zero32),
            _ => throw new ArgumentOutOfRangeException(nameof(messageName), messageName, null)
        };
    }
}
