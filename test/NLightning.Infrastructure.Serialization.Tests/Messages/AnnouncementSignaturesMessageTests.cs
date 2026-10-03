using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;
using static ChannelAnnouncementMessageTests;

/// <summary>
/// BOLT 7 announcement_signatures (259), plan G0-T2: a typed channel message, byte-exact round trips (trailing TLV
/// bytes kept), and its TLV extension read strictly (BOLT 1: unknown even types and malformed streams fail).
/// </summary>
public class AnnouncementSignaturesMessageTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly ShortChannelId s_scid = new(108, 1, 0);

    private readonly MessageSerializer _messageSerializer = new(NullLogger<MessageSerializer>.Instance,
                                                                new MessageTypeSerializerFactory(
                                                                    SerializerHelper.PayloadSerializerFactory,
                                                                    SerializerHelper.TlvConverterFactory,
                                                                    SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_AnnouncementSignatures_When_Serialized_Then_WireLayoutMatchesBolt7()
    {
        // Arrange
        var message = new AnnouncementSignaturesMessage(
            new AnnouncementSignaturesPayload(s_channelId, s_scid, Sig(1), Sig(2)));
        var expected = Concat([0x01, 0x03], s_channelId, s_scid, Sig(1), Sig(2));
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(expected, stream.ToArray());
        Assert.Equal(2 + AnnouncementSignaturesPayload.MinLength, expected.Length);
    }

    [Fact]
    public async Task Given_AnnouncementSignaturesBytes_When_Deserialized_Then_ChannelMessageWithTypedFields()
    {
        // Arrange
        var wire = Concat([0x01, 0x03], s_channelId, s_scid, Sig(1), Sig(2));
        using var input = new MemoryStream(wire);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(input);

        // Assert
        var typed = Assert.IsType<AnnouncementSignaturesMessage>(message);
        var channelMessage = Assert.IsAssignableFrom<IChannelMessage>(message);
        Assert.Equal(s_channelId, channelMessage.Payload.ChannelId);
        Assert.Equal(s_scid, typed.Payload.ShortChannelId);
        Assert.Equal(Sig(1), typed.Payload.NodeSignature.Value);
        Assert.Equal(Sig(2), typed.Payload.BitcoinSignature.Value);
        Assert.True(typed.Payload.ExtraData.IsEmpty);
    }

    [Fact]
    public async Task Given_UnknownOddTlvAfterTheFields_When_RoundTripped_Then_BytesAreIdentical()
    {
        // Arrange: type 1, length 2
        var wire = Concat([0x01, 0x03], s_channelId, s_scid, Sig(1), Sig(2), [0x01, 0x02, 0xca, 0xfe]);
        using var input = new MemoryStream(wire);

        // Act
        var message =
            Assert.IsType<AnnouncementSignaturesMessage>(await _messageSerializer.DeserializeMessageAsync(input));
        using var output = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(wire, output.ToArray());
        Assert.Equal(new byte[] { 0x01, 0x02, 0xca, 0xfe }, message.Payload.ExtraData.ToArray());
    }

    [Theory]
    [InlineData("0200")] // unknown even type
    [InlineData("0105ca")] // length past the end
    [InlineData("03000100")] // types not increasing
    public async Task Given_InvalidTlvExtension_When_Deserialized_Then_ThrowsMessageSerializationException(
        string extensionHex)
    {
        // Arrange
        var wire = Concat([0x01, 0x03], s_channelId, s_scid, Sig(1), Sig(2), Convert.FromHexString(extensionHex));
        using var input = new MemoryStream(wire);

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(AnnouncementSignaturesPayload.MinLength - 1)]
    public async Task Given_TruncatedAnnouncementSignatures_When_Deserialized_Then_ThrowsPayloadSerializationException(
        int length)
    {
        // Arrange
        var wire = Concat([0x01, 0x03], s_channelId, s_scid, Sig(1), Sig(2))[..(2 + length)];
        using var input = new MemoryStream(wire);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(input));
    }

    [Fact]
    public void Given_ShortSignature_When_Constructed_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new AnnouncementSignaturesPayload(
                                             s_channelId, s_scid, Sig(1), new byte[63]));
    }

    [Fact]
    public void Given_Bytes_When_TryParse_Then_ReturnsWhetherTheyFit()
    {
        // Act & Assert
        Assert.False(AnnouncementSignaturesPayload.TryParse(new byte[100], out var none));
        Assert.Null(none);
        Assert.True(AnnouncementSignaturesPayload.TryParse(Concat(s_channelId, s_scid, Sig(1), Sig(2)),
                                                           out var parsed));
        Assert.Equal(s_channelId, parsed.ChannelId);
    }
}