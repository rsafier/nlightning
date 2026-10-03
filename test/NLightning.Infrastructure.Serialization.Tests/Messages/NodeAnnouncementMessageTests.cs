using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;
using static ChannelAnnouncementMessageTests;

/// <summary>
/// BOLT 7 node_announcement (257), plan G0-T1: the Domain codec framed by the serializer, byte-exact round trips with
/// unknown trailing fields, the signed data from offset 64, and malformed input rejected.
/// </summary>
public class NodeAnnouncementMessageTests
{
    // IPv4 127.0.0.1:9735 then a Tor v3 descriptor (35 + 2 bytes): kept raw, decoded by the gossip layer
    private static readonly byte[] s_addresses =
        Concat([0x01, 127, 0, 0, 1, 0x26, 0x07], [0x04], Enumerable.Repeat((byte)0xab, 35).ToArray(), [0x26, 0x07]);

    private readonly MessageSerializer _messageSerializer = new(NullLogger<MessageSerializer>.Instance,
                                                                new MessageTypeSerializerFactory(
                                                                    SerializerHelper.PayloadSerializerFactory,
                                                                    SerializerHelper.TlvConverterFactory,
                                                                    SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_NodeAnnouncement_When_Serialized_Then_WireLayoutMatchesBolt7()
    {
        // Arrange
        var alias = NodeAnnouncementPayload.EncodeAlias("nltg");
        var message = new NodeAnnouncementMessage(
            new NodeAnnouncementPayload(Sig(7), new byte[] { 0x08, 0x00 }, 0x01020304, Key(0x55),
                                        new byte[] { 0x10, 0x20, 0x30 }, alias, s_addresses));
        var expected = Concat([0x01, 0x01], Sig(7), [0x00, 0x02, 0x08, 0x00], [0x01, 0x02, 0x03, 0x04], Key(0x55),
                              [0x10, 0x20, 0x30], alias, [0x00, (byte)s_addresses.Length], s_addresses);
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public async Task Given_NodeAnnouncementWithUnknownTrailingFields_When_RoundTripped_Then_BytesAreIdenticalAndSigned()
    {
        // Arrange
        var alias = NodeAnnouncementPayload.EncodeAlias("alice");
        var wire = Concat([0x01, 0x01], Sig(7), [0x00, 0x00], [0x65, 0x00, 0x00, 0x00], Key(0x55), [1, 2, 3], alias,
                          [0x00, (byte)s_addresses.Length], s_addresses, [0xfe, 0xed]);
        using var input = new MemoryStream(wire);

        // Act
        var message = Assert.IsType<NodeAnnouncementMessage>(await _messageSerializer.DeserializeMessageAsync(input));
        using var output = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(wire, output.ToArray());
        var payload = message.Payload;
        Assert.Equal(new byte[] { 0xfe, 0xed }, payload.ExtraData.ToArray());
        Assert.Empty(payload.Features.ToArray());
        Assert.Equal(0x65000000u, payload.Timestamp);
        Assert.Equal(Key(0x55), payload.NodeId);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.RgbColor.ToArray());
        Assert.Equal("alice", payload.GetAliasText());
        Assert.Equal(s_addresses, payload.Addresses.ToArray());
        Assert.Equal(wire[(2 + NodeAnnouncementPayload.SignatureLength)..], payload.GetSignedData());
        Assert.Equal(SHA256.HashData(SHA256.HashData(wire.AsSpan(2 + 64))), (byte[])payload.GetSignatureHash());
    }

    [Fact]
    public void Given_Announcement_When_Resigned_Then_SignatureHashIsUnchanged()
    {
        // Arrange
        var unsigned = new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty,
                                                   1, Key(0x55), new byte[3], new byte[32], ReadOnlyMemory<byte>.Empty);

        // Act
        var signed = unsigned.WithSignature(Sig(9));

        // Assert
        Assert.Equal(unsigned.GetSignatureHash(), signed.GetSignatureHash());
        Assert.Equal(Sig(9), signed.Signature.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(NodeAnnouncementPayload.SignatureLength)]
    [InlineData(NodeAnnouncementPayload.MinLength - 1)]
    public async Task Given_TruncatedNodeAnnouncement_When_Deserialized_Then_ThrowsPayloadSerializationException(
        int length)
    {
        // Arrange
        var payload = new NodeAnnouncementPayload(Sig(7), ReadOnlyMemory<byte>.Empty, 1, Key(0x55), new byte[3],
                                                  new byte[32], ReadOnlyMemory<byte>.Empty).GetBytes();
        using var stream = new MemoryStream(Concat([0x01, 0x01], payload)[..(2 + length)]);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(stream));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_LengthPrefixPastTheEnd_When_Deserialized_Then_ThrowsPayloadSerializationException(
        bool features)
    {
        // Arrange: flen or addrlen declares more bytes than remain
        var payload = new NodeAnnouncementPayload(Sig(7), ReadOnlyMemory<byte>.Empty, 1, Key(0x55), new byte[3],
                                                  new byte[32], s_addresses).GetBytes();
        var offset = features ? NodeAnnouncementPayload.SignatureLength : NodeAnnouncementPayload.MinLength - 2;
        payload[offset] = 0x7f;
        using var stream = new MemoryStream(Concat([0x01, 0x01], payload));

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(
            () => _messageSerializer.DeserializeMessageAsync(stream));
    }

    [Fact]
    public void Given_AliasLongerThan32Utf8Bytes_When_Encoded_Then_Throws()
    {
        // Act & Assert: 11 x 3-byte characters = 33 bytes
        Assert.Throws<ArgumentException>(() => NodeAnnouncementPayload.EncodeAlias(new string('€', 11)));
    }

    [Fact]
    public void Given_Utf8Alias_When_EncodedAndDecoded_Then_ZeroPaddedAndRestored()
    {
        // Act
        var field = NodeAnnouncementPayload.EncodeAlias("néud");
        var payload = new NodeAnnouncementPayload(Sig(7), ReadOnlyMemory<byte>.Empty, 1, Key(0x55), new byte[3], field,
                                                  ReadOnlyMemory<byte>.Empty);

        // Assert
        Assert.Equal(32, field.Length);
        Assert.All(field[5..], b => Assert.Equal(0, b));
        Assert.Equal("néud", payload.GetAliasText());
    }

    [Theory]
    [InlineData(2, 32)]
    [InlineData(3, 31)]
    public void Given_WrongColorOrAliasLength_When_Constructed_Then_Throws(int colorLength, int aliasLength)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new NodeAnnouncementPayload(
                                             Sig(7), ReadOnlyMemory<byte>.Empty, 1, Key(0x55),
                                             new byte[colorLength], new byte[aliasLength],
                                             ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Given_MalformedBytes_When_TryParse_Then_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(NodeAnnouncementPayload.TryParse(new byte[NodeAnnouncementPayload.MinLength - 1], out var none));
        Assert.Null(none);
    }
}