using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Infrastructure.Serialization.Wire;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Serialization.Messages;

/// <summary>
/// BOLT 4 <c>onion_message</c> (513): <c>point path_key || u16 len || len*byte onion_message_packet</c> (OM0-T1,
/// OM-W-01/02).
/// </summary>
public class OnionMessageMessageTests
{
    private static readonly byte[] s_pathKey =
        Convert.FromHexString("031195a8046dcbb8e17034bca630065e7a0982e4e36f6f7e5a8d4554e4846fcd99");

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new WireRegistry());

    public static TheoryData<int> VectorHops => new() { 0, 1, 2, 3 };

    [Theory]
    [MemberData(nameof(VectorHops))]
    public async Task Given_VectorOnionMessage_When_RoundTripped_Then_BytesAreIdentical(int hop)
    {
        // Arrange
        var wire = Convert.FromHexString(OnionMessageVectorData.AllOnionMessagesHex[hop]);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(new MemoryStream(wire));
        var bytes = await SerializeAsync(message!);

        // Assert
        var onionMessage = Assert.IsType<OnionMessageMessage>(message);
        Assert.Equal(MessageTypes.OnionMessage, onionMessage.Type);
        Assert.Equal(wire[2..35], (byte[])onionMessage.Payload.PathKey);
        Assert.Equal(1366, onionMessage.Payload.OnionMessagePacket.Length);
        Assert.Equal(wire, bytes);
    }

    [Fact]
    public async Task Given_AliceVectorMessage_When_Deserialized_Then_PacketIsTheVectorPacket()
    {
        // Arrange
        var wire = Convert.FromHexString(OnionMessageVectorData.AliceOnionMessageHex);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync<OnionMessageMessage>(new MemoryStream(wire));

        // Assert
        Assert.NotNull(message);
        Assert.Equal(Convert.FromHexString(OnionMessageVectorData.AlicePacketHex),
                     message.Payload.OnionMessagePacket.ToArray());
    }

    [Fact]
    public void Given_OnionMessage_When_Inspected_Then_ItIsNotAChannelMessage()
    {
        // Arrange
        var message = new OnionMessageMessage(new OnionMessagePayload(new CompactPubKey(s_pathKey), new byte[66]));

        // Act & Assert
        Assert.IsNotAssignableFrom<IChannelMessage>(message);
    }

    [Theory]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(1000)]
    [InlineData(32834)]
    public async Task Given_PacketLengthOtherThan1366_When_RoundTripped_Then_Accepted(int length)
    {
        // Arrange: only the writer SHOULD use 1366 or 32834; a reader takes any len from 66 up
        var packet = Enumerable.Range(0, length).Select(i => (byte)(i * 13)).ToArray();
        var wire = await SerializeAsync(new OnionMessageMessage(
                                            new OnionMessagePayload(new CompactPubKey(s_pathKey), packet)));

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(new MemoryStream(wire));

        // Assert
        var onionMessage = Assert.IsType<OnionMessageMessage>(message);
        Assert.Equal(packet, onionMessage.Payload.OnionMessagePacket.ToArray());
        Assert.Equal(2 + 33 + 2 + length, wire.Length);
    }

    [Fact]
    public async Task Given_OnionMessage_When_Serialized_Then_WireIsTypePathKeyLenAndPacket()
    {
        // Arrange
        var packet = Enumerable.Range(0, 66).Select(i => (byte)i).ToArray();
        var message = new OnionMessageMessage(new OnionMessagePayload(new CompactPubKey(s_pathKey), packet));

        // Act
        var bytes = await SerializeAsync(message);

        // Assert
        var expected = Convert.FromHexString("0201").Concat(s_pathKey).Concat(Convert.FromHexString("0042"))
                              .Concat(packet).ToArray();
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    public async Task Given_LenBelowPacketOverhead_When_Deserialized_Then_ThrowsPayloadSerializationException(
        int length)
    {
        // Arrange
        var wire = BuildWire(s_pathKey, (ushort)length, new byte[length]);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _messageSerializer.DeserializeMessageAsync(
                                                                    new MemoryStream(wire)));
    }

    [Fact]
    public async Task Given_TruncatedPacket_When_Deserialized_Then_ThrowsPayloadSerializationException()
    {
        // Arrange: says 1366 bytes, carries 1365
        var wire = BuildWire(s_pathKey, 1366, new byte[1365]);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _messageSerializer.DeserializeMessageAsync(
                                                                    new MemoryStream(wire)));
    }

    [Fact]
    public async Task Given_TruncatedPathKey_When_Deserialized_Then_ThrowsPayloadSerializationException()
    {
        // Arrange
        var wire = Convert.FromHexString("0201").Concat(s_pathKey[..20]).ToArray();

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _messageSerializer.DeserializeMessageAsync(
                                                                    new MemoryStream(wire)));
    }

    [Fact]
    public async Task Given_PathKeyWithoutPointPrefix_When_Deserialized_Then_ThrowsPayloadSerializationException()
    {
        // Arrange
        var badKey = (byte[])s_pathKey.Clone();
        badKey[0] = 0x04;
        var wire = BuildWire(badKey, 66, new byte[66]);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _messageSerializer.DeserializeMessageAsync(
                                                                    new MemoryStream(wire)));
    }

    [Fact]
    public async Task Given_UnknownOddTlvAfterPacket_When_Deserialized_Then_Ignored()
    {
        // Arrange: TLV type 3 length 1 after the packet
        var packet = new byte[66];
        var wire = BuildWire(s_pathKey, 66, packet).Concat(Convert.FromHexString("030101")).ToArray();

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(new MemoryStream(wire));

        // Assert
        var onionMessage = Assert.IsType<OnionMessageMessage>(message);
        Assert.Equal(packet, onionMessage.Payload.OnionMessagePacket.ToArray());
    }

    [Fact]
    public async Task Given_UnknownEvenTlvAfterPacket_When_Deserialized_Then_Rejected()
    {
        // Arrange: TLV type 2 length 1 after the packet (BOLT 1: an unknown even type fails the message)
        var wire = BuildWire(s_pathKey, 66, new byte[66]).Concat(Convert.FromHexString("020101")).ToArray();

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _messageSerializer.DeserializeMessageAsync(
                                                                    new MemoryStream(wire)));
    }

    [Fact]
    public void Given_PacketShorterThanOverhead_When_PayloadIsBuilt_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new OnionMessagePayload(new CompactPubKey(s_pathKey), new byte[65]));
    }

    private static byte[] BuildWire(byte[] pathKey, ushort length, byte[] packet)
    {
        return Convert.FromHexString("0201").Concat(pathKey).Concat(new[] { (byte)(length >> 8), (byte)length })
                      .Concat(packet).ToArray();
    }

    private async Task<byte[]> SerializeAsync(IMessage message)
    {
        var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }
}