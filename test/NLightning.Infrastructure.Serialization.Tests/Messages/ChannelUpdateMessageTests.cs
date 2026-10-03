using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

public class ChannelUpdateMessageTests
{
    /// <summary>
    /// A channel_update payload (without the type) sent by LND 0.20 (Docker <c>custom_lnd</c>, node "alice",
    /// 029fa86deeed231f8ce4a8650bd7001cc39b63ddba32fc407533b17143866e7f83) to our node right after channel_ready.
    /// The signature check against that node id is in <c>LocalLightningSignerNodeMessageTests</c>.
    /// </summary>
    private const string LndChannelUpdateHex =
        "69A08F20E845FAA2EFC785E7492793CF548551779FFB3D53CD2D9DD9E4A7F98A155F5486B71ABE26C5209D1778C4BD8849F4E18B5F540C"
      + "8AA62DB384B04DE99E06226E46111A0B59CAAF126043EB5BBF28C34F3A5E332A1FC7B2B73CF188910F0000FD00000100016AB6CAA9010100"
      + "5000000000000003E8000003E800000001000000002FAF0800";

    private readonly MessageSerializer _messageSerializer;

    public ChannelUpdateMessageTests()
    {
        var messageTypeSerializerFactory =
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer);
        _messageSerializer = new MessageSerializer(NullLogger<MessageSerializer>.Instance,
                                                   messageTypeSerializerFactory);
    }

    [Fact]
    public async Task Given_LndChannelUpdateBytes_When_DeserializeMessageAsync_Then_ReturnsTypedFields()
    {
        // Arrange
        using var stream = new MemoryStream(Convert.FromHexString("0102" + LndChannelUpdateHex));

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        var channelUpdate = Assert.IsType<ChannelUpdateMessage>(message);
        var payload = channelUpdate.Payload;
        Assert.Equal(MessageTypes.ChannelUpdate, channelUpdate.Type);
        Assert.Equal(Convert.FromHexString(LndChannelUpdateHex)[..64], payload.Signature.Value);
        Assert.Equal(ChainConstants.Regtest, payload.ChainHash);
        Assert.Equal(new ShortChannelId(253, 1, 1), payload.ShortChannelId);
        Assert.Equal(0x6AB6CAA9u, payload.Timestamp);
        Assert.Equal(ChannelUpdatePayload.MessageFlagMustBeOne, payload.MessageFlags);
        Assert.False(payload.DontForward);
        Assert.True(payload.Direction);
        Assert.False(payload.IsDisabled);
        Assert.Equal(80, payload.CltvExpiryDelta);
        Assert.Equal(1_000UL, payload.HtlcMinimumMsat);
        Assert.Equal(1_000u, payload.FeeBaseMsat);
        Assert.Equal(1u, payload.FeeProportionalMillionths);
        Assert.Equal(800_000_000UL, payload.HtlcMaximumMsat);
        Assert.True(payload.ExtraData.IsEmpty);
        Assert.Null(channelUpdate.Extension);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_LndChannelUpdate_When_RoundTripped_Then_BytesAreIdentical()
    {
        // Arrange
        var wire = Convert.FromHexString("0102" + LndChannelUpdateHex);
        using var input = new MemoryStream(wire);
        var message = await _messageSerializer.DeserializeMessageAsync(input);
        using var output = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message!, output);

        // Assert
        Assert.Equal(wire, output.ToArray());
    }

    [Fact]
    public async Task Given_ChannelUpdateMessage_When_SerializedAndDeserialized_Then_AllFieldsMatch()
    {
        // Arrange
        var signature = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        var expected = new ChannelUpdatePayload(signature, ChainConstants.Main, new ShortChannelId(700_000, 1234, 5),
                                                1_790_000_000,
                                                ChannelUpdatePayload.MessageFlagMustBeOne
                                              | ChannelUpdatePayload.MessageFlagDontForward,
                                                ChannelUpdatePayload.ChannelFlagDirection
                                              | ChannelUpdatePayload.ChannelFlagDisable, 144, 1, 2_000, 150,
                                                ulong.MaxValue);
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(new ChannelUpdateMessage(expected), stream);
        stream.Position = 0;
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        Assert.Equal(2 + ChannelUpdatePayload.MinLength, stream.Length);
        var actual = Assert.IsType<ChannelUpdateMessage>(message).Payload;
        Assert.Equal(expected.Signature, actual.Signature);
        Assert.Equal(expected.ChainHash, actual.ChainHash);
        Assert.Equal(expected.ShortChannelId, actual.ShortChannelId);
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.MessageFlags, actual.MessageFlags);
        Assert.Equal(expected.ChannelFlags, actual.ChannelFlags);
        Assert.True(actual.DontForward);
        Assert.True(actual.IsDisabled);
        Assert.True(actual.Direction);
        Assert.Equal(expected.CltvExpiryDelta, actual.CltvExpiryDelta);
        Assert.Equal(expected.HtlcMinimumMsat, actual.HtlcMinimumMsat);
        Assert.Equal(expected.FeeBaseMsat, actual.FeeBaseMsat);
        Assert.Equal(expected.FeeProportionalMillionths, actual.FeeProportionalMillionths);
        Assert.Equal(expected.HtlcMaximumMsat, actual.HtlcMaximumMsat);
        Assert.Equal(expected.GetSignatureHash(), actual.GetSignatureHash());
    }

    [Fact]
    public async Task Given_ChannelUpdateWithUnknownTrailingFields_When_RoundTripped_Then_ExtraDataIsKeptAndSigned()
    {
        // Arrange: BOLT 7 signs "including unknown fields following fee_proportional_millionths"
        var extra = Convert.FromHexString("0102030405");
        var wire = Convert.FromHexString("0102" + LndChannelUpdateHex + "0102030405");
        using var input = new MemoryStream(wire);

        // Act
        var message = Assert.IsType<ChannelUpdateMessage>(await _messageSerializer.DeserializeMessageAsync(input));
        using var output = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(extra, message.Payload.ExtraData.ToArray());
        Assert.Equal(wire, output.ToArray());
        Assert.Equal(wire[(2 + ChannelUpdatePayload.SignatureLength)..], message.Payload.GetSignedData());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(ChannelUpdatePayload.MinLength - 1)]
    public async Task Given_TruncatedChannelUpdate_When_DeserializeMessageAsync_Then_ThrowsPayloadSerializationException(
        int length)
    {
        // Arrange
        var wire = Convert.FromHexString("0102" + LndChannelUpdateHex)[..(2 + length)];
        using var stream = new MemoryStream(wire);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _messageSerializer.DeserializeMessageAsync(stream));
    }
}