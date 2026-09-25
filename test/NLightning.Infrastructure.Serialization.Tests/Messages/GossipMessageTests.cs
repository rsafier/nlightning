using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Factories;
using Helpers;
using Serialization.Messages;

public class GossipMessageTests
{
    private readonly MessageSerializer _messageSerializer;

    public GossipMessageTests()
    {
        var messageTypeSerializerFactory =
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer);
        _messageSerializer = new MessageSerializer(NullLogger<MessageSerializer>.Instance,
                                                   messageTypeSerializerFactory);
    }

    public static TheoryData<ushort, Type> GossipTypes => new()
    {
        { 256, typeof(ChannelAnnouncementMessage) },
        { 257, typeof(NodeAnnouncementMessage) },
        { 258, typeof(ChannelUpdateMessage) },
        { 259, typeof(AnnouncementSignaturesMessage) }
    };

    [Theory]
    [MemberData(nameof(GossipTypes))]
    public async Task Given_GossipMessageBytes_When_DeserializeMessageAsync_Then_ReturnsGossipMessageWithRawPayload(
        ushort type, Type expectedMessageType)
    {
        // Arrange
        var payloadBytes = Convert.FromHexString("0102030405060708090a0b0c0d0e0f");
        var messageBytes = new byte[2 + payloadBytes.Length];
        messageBytes[0] = (byte)(type >> 8);
        messageBytes[1] = (byte)type;
        payloadBytes.CopyTo(messageBytes, 2);
        using var stream = new MemoryStream(messageBytes);

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.IsType(expectedMessageType, message);
        Assert.Equal((MessageTypes)type, message.Type);
        var gossipMessage = Assert.IsAssignableFrom<GossipMessage>(message);
        Assert.Equal(payloadBytes, gossipMessage.Payload.Data.ToArray());
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_ChannelUpdateMessage_When_SerializeAsync_Then_WritesTypeAndRawPayload()
    {
        // Arrange
        var payloadBytes = Convert.FromHexString("aabbccdd");
        var message = new ChannelUpdateMessage(new GossipPayload(payloadBytes));
        using var stream = new MemoryStream();

        // Act
        await _messageSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(Convert.FromHexString("0102aabbccdd"), stream.ToArray());
    }
}