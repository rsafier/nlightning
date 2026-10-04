using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.Payloads;

using Converters;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Helpers;

public class ErrorPayloadSerializerTests
{
    [Fact]
    public async Task Given_ValidPayload_When_Serializing_Then_ReturnsCorrectValues()
    {
        // Given
        var errorMessageTypeSerializer =
            SerializerHelper.MessageTypeSerializerFactory.GetSerializer<ErrorMessage>()!;
        var errorPayload = new ErrorPayload(ChannelId.Zero);
        using var memoryStream = new MemoryStream();

        // When
        await errorMessageTypeSerializer.SerializeAsync(new ErrorMessage(errorPayload), memoryStream);

        // Then
        memoryStream.Seek(0, SeekOrigin.Begin);
        var expectedLengthBytes = new byte[2];
        _ = await memoryStream.ReadAsync(expectedLengthBytes.AsMemory(0, 2), TestContext.Current.CancellationToken);

        Assert.Equal(0, EndianBitConverter.ToUInt16BigEndian(expectedLengthBytes));
    }
}