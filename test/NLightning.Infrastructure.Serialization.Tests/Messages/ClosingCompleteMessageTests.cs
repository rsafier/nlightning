using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;
using Serialization.Messages.Types;

/// <summary>
/// <c>closing_complete</c> (40) and <c>closing_sig</c> (41) on the wire (BOLT 2 <c>option_simple_close</c>, B2-SC-W01):
/// byte layout, round trips, and the strict <c>closing_tlvs</c> reader (types 1/2/3, 64-byte signatures).
/// </summary>
public class ClosingCompleteMessageTests
{
    internal const string ChannelIdHex = "1111111111111111111111111111111111111111111111111111111111111111";
    internal const string CloserScriptHex = "0014A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";
    internal const string CloseeScriptHex = "0020B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0B0";

    internal const string Sig1Hex =
        "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320";

    internal const string Sig3Hex =
        "6C255404781C32E1BAB22132727738ABC76D39C1AB9D1FDAA206881D947D50179C36CD3B22C059800BE2BA8AEC68C557326C18805D31C510CF4E41899E7B77C2";

    /// <summary>
    /// channel_id, u16 22 + closer (P2WPKH), u16 34 + closee (P2WSH), fee 1000 sat, locktime 500: the fixed part.
    /// </summary>
    internal const string FixedHex = ChannelIdHex + "0016" + CloserScriptHex + "0022" + CloseeScriptHex
                                   + "00000000000003E8" + "000001F4";

    /// <summary>closer_output_only (1) and closer_and_closee_outputs (3).</summary>
    private const string ClosingCompleteHex = FixedHex + "0140" + Sig1Hex + "0340" + Sig3Hex;

    private readonly ClosingCompleteMessageTypeSerializer _serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvStreamSerializer);

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer));

    internal static ClosingCompletePayload Payload() =>
        new(new ChannelId(Convert.FromHexString(ChannelIdHex)), new BitcoinScript(Convert.FromHexString(CloserScriptHex)),
            new BitcoinScript(Convert.FromHexString(CloseeScriptHex)), LightningMoney.Satoshis(1_000), 500);

    [Fact]
    public async Task Given_ClosingComplete_When_SerializeAsync_Then_BoltLayout()
    {
        // Arrange
        var message = new ClosingCompleteMessage(Payload(),
                                                 new ClosingSignatures(
                                                     new CompactSignature(Convert.FromHexString(Sig1Hex)),
                                                     null,
                                                     new CompactSignature(Convert.FromHexString(Sig3Hex))));
        using var stream = new MemoryStream();

        // Act
        await _serializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(ClosingCompleteHex, Convert.ToHexString(stream.ToArray()));
    }

    [Fact]
    public async Task Given_Bytes_When_DeserializeAsync_Then_EveryField()
    {
        // Arrange
        using var stream = new MemoryStream(Convert.FromHexString(ClosingCompleteHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(MessageTypes.ClosingComplete, message.Type);
        Assert.Equal(Convert.FromHexString(ChannelIdHex), (byte[])message.Payload.ChannelId);
        Assert.Equal(Convert.FromHexString(CloserScriptHex), (byte[])message.Payload.CloserScriptPubKey);
        Assert.Equal(Convert.FromHexString(CloseeScriptHex), (byte[])message.Payload.CloseeScriptPubKey);
        Assert.Equal(1_000L, message.Payload.FeeSatoshis.Satoshi);
        Assert.Equal(500U, message.Payload.LockTime);
        Assert.Equal([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs],
                     message.Signatures.Kinds);
        Assert.Equal(Convert.FromHexString(Sig1Hex), (byte[])message.Signatures.CloserOutputOnly!);
        Assert.Null(message.Signatures.CloseeOutputOnly);
        Assert.Equal(Convert.FromHexString(Sig3Hex), (byte[])message.Signatures.CloserAndCloseeOutputs!);
    }

    [Fact]
    public async Task Given_WireMessage_When_RoundTripThroughMessageSerializer_Then_SameBytesAndType40()
    {
        // Arrange
        var wire = Convert.FromHexString("0028" + ClosingCompleteHex);
        using var input = new MemoryStream(wire);

        // Act
        var message = Assert.IsType<ClosingCompleteMessage>(await _messageSerializer.DeserializeMessageAsync(input));
        using var output = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(wire, output.ToArray());
    }

    [Fact]
    public async Task Given_NoTlvs_When_DeserializeAsync_Then_NoSignatures()
    {
        // Arrange
        using var stream = new MemoryStream(Convert.FromHexString(FixedHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Empty(message.Signatures.Kinds);
        Assert.Null(message.Extension);
    }

    [Fact]
    public async Task Given_OpReturnScriptAndZeroFee_When_RoundTrip_Then_Kept()
    {
        // Arrange: OP_RETURN closer script (option_simple_close), fee 0, max locktime
        BitcoinScript opReturn = new([0x6a, 0x06, 1, 2, 3, 4, 5, 6]);
        var message = new ClosingCompleteMessage(
            new ClosingCompletePayload(ChannelId.Zero, opReturn, BitcoinScript.Empty, LightningMoney.Zero,
                                       uint.MaxValue),
            ClosingSignatures.Single(ClosingSigKind.CloseeOutputOnly,
                                     new CompactSignature(Convert.FromHexString(Sig1Hex))));
        using var stream = new MemoryStream();
        await _serializer.SerializeAsync(message, stream);
        stream.Position = 0;

        // Act
        var decoded = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(opReturn, decoded.Payload.CloserScriptPubKey);
        Assert.Equal(0, decoded.Payload.CloseeScriptPubKey.Length);
        Assert.Equal(0L, decoded.Payload.FeeSatoshis.Satoshi);
        Assert.Equal(uint.MaxValue, decoded.Payload.LockTime);
        Assert.Equal([ClosingSigKind.CloseeOutputOnly], decoded.Signatures.Kinds);
    }

    [Fact]
    public async Task Given_UnknownOddTlv_When_DeserializeAsync_Then_Ignored()
    {
        // Arrange: type 5 (odd, unknown)
        using var stream = new MemoryStream(Convert.FromHexString(FixedHex + "0340" + Sig3Hex + "0501FF"));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal([ClosingSigKind.CloserAndCloseeOutputs], message.Signatures.Kinds);
    }

    [Theory]
    [InlineData("0401FF")] // unknown even type: BOLT 1 MUST fail
    [InlineData("033F")] // signature of 63 bytes
    [InlineData("0340" + Sig3Hex + "0140" + Sig1Hex)] // types not increasing
    public async Task Given_BadTlvs_When_DeserializeAsync_Then_Throws(string tlvHex)
    {
        // Arrange
        var hex = tlvHex == "033F" ? FixedHex + "033F" + Sig3Hex[..126] : FixedHex + tlvHex;
        using var stream = new MemoryStream(Convert.FromHexString(hex));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _serializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_TruncatedPayload_When_DeserializeAsync_Then_Throws()
    {
        // Arrange: the locktime is cut
        using var stream = new MemoryStream(Convert.FromHexString(FixedHex[..^2]));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _serializer.DeserializeAsync(stream));
    }
}