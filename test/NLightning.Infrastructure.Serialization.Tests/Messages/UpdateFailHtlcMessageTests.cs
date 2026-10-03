using NLightning.Domain.Channels.ValueObjects;
using NLightning.Infrastructure.Exceptions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Helpers;
using Serialization.Messages.Types;

public class UpdateFailHtlcMessageTests
{
    private const string PayloadHex =
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000F567CBDADB00B825448B2E414487D73";

    private readonly UpdateFailHtlcMessageTypeSerializer _updateFailHtlcMessageTypeSerializer;

    public UpdateFailHtlcMessageTests()
    {
        _updateFailHtlcMessageTypeSerializer =
            new UpdateFailHtlcMessageTypeSerializer(SerializerHelper.PayloadSerializerFactory,
                                                    SerializerHelper.TlvConverterFactory,
                                                    SerializerHelper.TlvStreamSerializer);
    }

    #region Deserialize

    [Fact]
    public async Task Given_ValidStream_When_DeserializeAsync_Then_ReturnsUpdateFailHtlcMessage()
    {
        // Arrange
        var expectedChannelId = ChannelId.Zero;
        var expectedId = 0UL;
        var expectedReason = Convert.FromHexString("567cbdadb00b825448b2e414487d73");
        var expectedLen = expectedReason.Length;
        var stream = new MemoryStream(Convert.FromHexString(
                                          "00000000000000000000000000000000000000000000000000000000000000000000000000000000000F567CBDADB00B825448B2E414487D73"));

        // Act
        var message = await _updateFailHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(expectedChannelId, message.Payload.ChannelId);
        Assert.Equal(expectedId, message.Payload.Id);
        Assert.Equal(expectedLen, message.Payload.Len);
        Assert.Equal(expectedReason, message.Payload.Reason);
        Assert.Null(message.Extension);
    }

    #endregion

    #region Serialize

    [Fact]
    public async Task Given_ValidPayloadWith_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        var id = 0UL;
        var expectedReason = Convert.FromHexString("567cbdadb00b825448b2e414487d73");
        var message = new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(channelId, id, expectedReason));
        var stream = new MemoryStream();
        var expectedBytes =
            Convert.FromHexString(
                "00000000000000000000000000000000000000000000000000000000000000000000000000000000000F567CBDADB00B825448B2E414487D73");

        // Act
        await _updateFailHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    #endregion

    #region attribution_data (TLV 1)

    [Fact]
    public async Task Given_MessageWithAttributionData_When_RoundTripping_Then_TlvIsWrittenAndRead()
    {
        // Arrange
        var attributionData = Enumerable.Range(0, AttributionDataTlv.ValueLength).Select(i => (byte)(i * 7)).ToArray();
        var message = new UpdateFailHtlcMessage(
            new UpdateFailHtlcPayload(ChannelId.Zero, 0, Convert.FromHexString("567cbdadb00b825448b2e414487d73")),
            new AttributionDataTlv(attributionData));
        var stream = new MemoryStream();

        // Act
        await _updateFailHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        var bytes = stream.ToArray();
        stream.Position = 0;
        var result = await _updateFailHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert: type 1, length 920 (fd 0398), then the value
        Assert.Equal(PayloadHex + "01FD0398" + Convert.ToHexString(attributionData), Convert.ToHexString(bytes));
        Assert.NotNull(result.AttributionDataTlv);
        Assert.Equal(attributionData, result.AttributionDataTlv.AttributionData);
        Assert.NotNull(result.Extension);
    }

    [Fact]
    public async Task Given_AttributionDataOfWrongLength_When_DeserializeAsync_Then_Throws()
    {
        // Arrange: BOLT 1, a known fixed-size type with another length fails the stream
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "010401020304"));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() =>
                                                                    _updateFailHtlcMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownEvenTlv_When_DeserializeAsync_Then_Throws()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "0201FF"));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() =>
                                                                    _updateFailHtlcMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownOddTlv_When_DeserializeAsync_Then_IgnoresIt()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "0501FF"));

        // Act
        var message = await _updateFailHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.AttributionDataTlv);
        Assert.Equal(15, message.Payload.Len);
    }

    #endregion
}