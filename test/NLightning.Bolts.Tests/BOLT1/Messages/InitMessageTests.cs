using System.Text;

namespace NLightning.Bolts.Tests.BOLT1.Messages;

using Bolts.BOLT1.Messages;
using Bolts.BOLT1.Payloads;
using Bolts.BOLT9;
using Bolts.Exceptions;
using Common.Constants;
using Common.TLVs;
using Common.Types;

public class InitMessageTests
{
    [Fact]
    public async Task Given_ValidStreamWithPayloadAndExtension_When_DeserializeAsync_IsCalled_Then_ReturnsInitMessageWithCorrectData()
    {
        // Arrange
        var expectedPayload = new InitPayload(new Features());
        var expectedExtension = new TLVStream();
        var expectedTlv = new NetworksTLV([ChainConstants.Main]);
        expectedExtension.Add(expectedTlv);
        var stream = await CreateStreamFromPayloadAndExtensionAsync(expectedPayload, expectedExtension);

        // Act
        var initMessage = await InitMessage.DeserializeAsync(stream);

        // Assert
        TLV? tlv = null;
        Assert.NotNull(initMessage);
        Assert.Equal(expectedPayload.Features.ToString(), initMessage.Payload.Features.ToString());
        var hasTlv = initMessage.Extension?.TryGetTlv(TLVConstants.NETWORKS, out tlv);
        Assert.True(hasTlv);
        Assert.Equal(expectedTlv.Value, tlv!.Value);
    }

    [Fact]
    public async Task Given_ValidStreamWithOnlyPayload_When_DeserializeAsync_IsCalled_Then_ReturnsInitMessageWithNullExtension()
    {
        // Arrange
        var expectedPayload = new InitPayload(new Features());
        var stream = await Helpers.CreateStreamFromPayloadAsync(expectedPayload);

        // Act
        var initMessage = await InitMessage.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(initMessage);
        Assert.Equal(expectedPayload.Features.ToString(), initMessage.Payload.Features.ToString());
        Assert.Null(initMessage.Extension);
    }

    [Fact]
    public async Task Given_InvalidStreamContent_When_DeserializeAsync_IsCalled_Then_ThrowsMessageSerializationException()
    {
        // Arrange
        var invalidStream = new MemoryStream(Encoding.UTF8.GetBytes("Invalid content"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => InitMessage.DeserializeAsync(invalidStream));
    }

    private static async Task<Stream> CreateStreamFromPayloadAndExtensionAsync(InitPayload payload, TLVStream extension)
    {
        var stream = new MemoryStream();
        await payload.SerializeAsync(stream);
        await extension.SerializeAsync(stream);
        stream.Position = 0;
        return stream;
    }
}