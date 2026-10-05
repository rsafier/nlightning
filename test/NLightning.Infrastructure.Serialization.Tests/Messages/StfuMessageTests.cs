using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Factories;
using Helpers;
using Serialization.Messages;

public class StfuMessageTests
{
    private readonly IMessageTypeSerializer<StfuMessage> _stfuMessageTypeSerializer =
        SerializerHelper.MessageTypeSerializerFactory.GetSerializer<StfuMessage>()!;

    [Fact]
    public async Task Given_ValidStream_When_DeserializeAsync_Then_ReturnsStfuMessage()
    {
        // Arrange
        var expectedChannelId = ChannelId.Zero;
        var expectedInitiator = true;

        var stream =
            new MemoryStream(
                Convert.FromHexString("000000000000000000000000000000000000000000000000000000000000000001"));

        // Act
        var message = await _stfuMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Equal(expectedChannelId, message.Payload.ChannelId);
        Assert.Equal(expectedInitiator, message.Payload.Initiator);
    }

    [Fact]
    public async Task Given_GivenValidPayload_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        var message = new StfuMessage(new StfuPayload(channelId, true));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString("000000000000000000000000000000000000000000000000000000000000000001");

        // Act
        await _stfuMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_StfuWireBytes_When_DeserializeMessageAsync_Then_ChannelMessageThatRoundTrips()
    {
        // Arrange: BOLT 2 "Channel Quiescence": type 2 (stfu) = channel_id || u8 initiator; a channel message
        // (splicing plan Q-W-01), so the factory must map type 2 or the even type would kill the connection
        var messageTypeSerializerFactory =
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer);
        var messageSerializer = new MessageSerializer(NullLogger<MessageSerializer>.Instance,
                                                      messageTypeSerializerFactory);
        var wire = Convert.FromHexString("0002" + new string('4', 62) + "4200");
        using var input = new MemoryStream(wire);

        // Act
        var message = await messageSerializer.DeserializeMessageAsync(input);
        using var output = new MemoryStream();
        await messageSerializer.SerializeAsync(message!, output);

        // Assert
        var stfu = Assert.IsType<StfuMessage>(message);
        Assert.IsAssignableFrom<IChannelMessage>(stfu);
        Assert.Equal(MessageTypes.Stfu, stfu.Type);
        Assert.Equal(new ChannelId(Enumerable.Repeat((byte)0x44, 31).Append((byte)0x42).ToArray()),
                     stfu.Payload.ChannelId);
        Assert.False(stfu.Payload.Initiator);
        Assert.Equal(wire, output.ToArray());
    }
}