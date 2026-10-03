using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

public class MessageSerializerTests
{
    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_WireTypeMatchingT_When_DeserializeMessageAsyncOfT_Then_ReturnsMessage()
    {
        // Arrange
        var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(new WarningMessage(new ErrorPayload("oops")), stream);
        stream.Position = 0;

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync<WarningMessage>(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Equal("oops", System.Text.Encoding.UTF8.GetString(message.Payload.Data!));
    }

    [Fact]
    public async Task Given_WireTypeNotMatchingT_When_DeserializeMessageAsyncOfT_Then_ThrowsInvalidMessageException()
    {
        // Arrange (error and warning share a payload layout, so only the wire type tells them apart)
        var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(new ErrorMessage(new ErrorPayload("oops")), stream);
        stream.Position = 0;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidMessageException>(() =>
            _messageSerializer.DeserializeMessageAsync<WarningMessage>(stream));
    }
}