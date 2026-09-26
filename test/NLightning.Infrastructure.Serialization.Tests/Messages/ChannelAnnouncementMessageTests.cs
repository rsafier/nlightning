using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// BOLT 7 channel_announcement (256), plan G0-T1: the Domain codec framed by the serializer, byte-exact round trips
/// with unknown trailing fields, the signed data from offset 256, and malformed input rejected. Captured LND/CLN
/// messages are checked in <c>Integration.Tests/BOLT7/Bolt7CapturedVectorTests</c>.
/// </summary>
public class ChannelAnnouncementMessageTests
{
    private static readonly ShortChannelId s_scid = new(103, 1, 0);

    private readonly MessageSerializer _messageSerializer = new(NullLogger<MessageSerializer>.Instance,
                                                                new MessageTypeSerializerFactory(
                                                                    SerializerHelper.PayloadSerializerFactory,
                                                                    SerializerHelper.TlvConverterFactory,
                                                                    SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_AnnouncementWithFeaturesAndExtraData_When_Serialized_Then_WireLayoutMatchesBolt7()
    {
        // Arrange
        var features = new byte[] { 0x01, 0x00 };
        var extra = new byte[] { 0xaa, 0xbb, 0xcc };
        var message = new ChannelAnnouncementMessage(CreatePayload(features, extra));
        var expected = Concat([0x01, 0x00], Sig(1), Sig(2), Sig(3), Sig(4), [0x00, 0x02], features,
                              ChainConstants.Regtest, s_scid, Key(0x11), Key(0x22), Key(0x33), Key(0x44), extra);
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public async Task Given_AnnouncementWithUnknownTrailingFields_When_RoundTripped_Then_BytesAreIdenticalAndSigned()
    {
        // Arrange: BOLT 7 signs from offset 256 "until the end of the message", unknown fields included
        var wire = Concat([0x01, 0x00], Sig(1), Sig(2), Sig(3), Sig(4), [0x00, 0x01], [0x80], ChainConstants.Regtest,
                          s_scid, Key(0x11), Key(0x22), Key(0x33), Key(0x44), [0x01, 0x02, 0x03, 0x04, 0x05]);
        using var input = new MemoryStream(wire);

        // Act
        var message =
            Assert.IsType<ChannelAnnouncementMessage>(await _messageSerializer.DeserializeMessageAsync(input));
        using var output = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(wire, output.ToArray());
        var payload = message.Payload;
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, payload.ExtraData.ToArray());
        Assert.Equal(new byte[] { 0x80 }, payload.Features.ToArray());
        Assert.Equal(s_scid, payload.ShortChannelId);
        Assert.Equal(ChainConstants.Regtest, payload.ChainHash);
        Assert.Equal(Key(0x11), payload.NodeId1);
        Assert.Equal(Key(0x22), payload.NodeId2);
        Assert.Equal(Key(0x33), payload.BitcoinKey1);
        Assert.Equal(Key(0x44), payload.BitcoinKey2);
        Assert.Equal(Sig(1), payload.NodeSignature1.Value);
        Assert.Equal(Sig(2), payload.NodeSignature2.Value);
        Assert.Equal(Sig(3), payload.BitcoinSignature1.Value);
        Assert.Equal(Sig(4), payload.BitcoinSignature2.Value);
        Assert.Equal(wire[(2 + ChannelAnnouncementPayload.SignaturesLength)..], payload.GetSignedData());
        Assert.Equal(SHA256.HashData(SHA256.HashData(wire.AsSpan(2 + 256))), (byte[])payload.GetSignatureHash());
    }

    [Fact]
    public void Given_Announcement_When_SignaturesReplaced_Then_SignatureHashIsUnchanged()
    {
        // Arrange
        var unsigned = CreatePayload([], [], ChannelAnnouncementPayload.EmptySignature);

        // Act
        var signed = unsigned.WithSignatures(Sig(1), Sig(2), Sig(3), Sig(4));

        // Assert
        Assert.Equal(unsigned.GetSignatureHash(), signed.GetSignatureHash());
        Assert.Equal(Sig(3), signed.BitcoinSignature1.Value);
        Assert.Equal(new byte[64], unsigned.NodeSignature1.Value);
    }

    [Fact]
    public void Given_Payload_When_SignatureReadAndEdited_Then_PayloadIsUnchanged()
    {
        // Arrange
        var payload = CreatePayload([], []);

        // Act
        payload.NodeSignature1.Value[0] ^= 0xff;

        // Assert
        Assert.Equal(Sig(1), payload.NodeSignature1.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ChannelAnnouncementPayload.SignaturesLength)]
    [InlineData(ChannelAnnouncementPayload.MinLength - 1)]
    public async Task Given_TruncatedAnnouncement_When_Deserialized_Then_ThrowsPayloadSerializationException(
        int length)
    {
        // Arrange
        var wire = Concat([0x01, 0x00], CreatePayload([], []).GetBytes())[..(2 + length)];
        using var stream = new MemoryStream(wire);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(stream));
    }

    [Fact]
    public async Task Given_FeatureLengthPastTheEnd_When_Deserialized_Then_ThrowsPayloadSerializationException()
    {
        // Arrange: len = 0xffff, far more than the bytes left
        var payload = CreatePayload([], []).GetBytes();
        payload[ChannelAnnouncementPayload.SignaturesLength] = 0xff;
        payload[ChannelAnnouncementPayload.SignaturesLength + 1] = 0xff;
        using var stream = new MemoryStream(Concat([0x01, 0x00], payload));

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(stream));
    }

    [Fact]
    public async Task Given_NodeIdNotACompressedPoint_When_Deserialized_Then_ThrowsPayloadSerializationException()
    {
        // Arrange: node_id_1 with a 0x04 prefix
        var payload = CreatePayload([], []).GetBytes();
        payload[ChannelAnnouncementPayload.SignaturesLength + 2 + 32 + 8] = 0x04;
        using var stream = new MemoryStream(Concat([0x01, 0x00], payload));

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(stream));
    }

    [Fact]
    public void Given_ShortSignature_When_Constructed_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new ChannelAnnouncementPayload(
                                             new CompactSignature(new byte[63]), Sig(2), Sig(3), Sig(4),
                                             ReadOnlyMemory<byte>.Empty, ChainConstants.Regtest, s_scid, Key(0x11),
                                             Key(0x22), Key(0x33), Key(0x44)));
    }

    [Fact]
    public void Given_MalformedBytes_When_TryParse_Then_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(ChannelAnnouncementPayload.TryParse(new byte[10], out var none));
        Assert.Null(none);
        Assert.True(ChannelAnnouncementPayload.TryParse(CreatePayload([], []).GetBytes(), out var parsed));
        Assert.Equal(s_scid, parsed.ShortChannelId);
    }

    private static ChannelAnnouncementPayload CreatePayload(byte[] features, byte[] extra,
                                                            CompactSignature? nodeSignature1 = null) =>
        new(nodeSignature1 ?? Sig(1), Sig(2), Sig(3), Sig(4), features, ChainConstants.Regtest, s_scid, Key(0x11),
            Key(0x22), Key(0x33), Key(0x44), extra);

    internal static byte[] Sig(byte fill) => Enumerable.Repeat(fill, 64).ToArray();

    internal static CompactPubKey Key(byte fill)
    {
        var key = Enumerable.Repeat(fill, 33).ToArray();
        key[0] = 0x02;
        return new CompactPubKey(key);
    }

    internal static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}