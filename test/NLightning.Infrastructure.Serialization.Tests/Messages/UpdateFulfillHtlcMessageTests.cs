using NLightning.Domain.Channels.ValueObjects;
using NLightning.Infrastructure.Exceptions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Helpers;
using Serialization.Messages.Types;

public class UpdateFulfillHtlcMessageTests
{
    private const string PayloadHex =
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000567CBDADB00B825448B2E414487D73A97F657F0634166D3AB3F3A2CC1042EDA5";

    private readonly UpdateFulfillHtlcMessageTypeSerializer _fulfillHtlcMessageTypeSerializer;

    public UpdateFulfillHtlcMessageTests()
    {
        _fulfillHtlcMessageTypeSerializer =
            new UpdateFulfillHtlcMessageTypeSerializer(SerializerHelper.PayloadSerializerFactory,
                                                       SerializerHelper.TlvConverterFactory,
                                                       SerializerHelper.TlvStreamSerializer);
    }

    #region Deserialize

    [Fact]
    public async Task Given_ValidStream_When_DeserializeAsync_Then_ReturnsUpdateFulfillHtlcMessage()
    {
        // Arrange
        var expectedChannelId = ChannelId.Zero;
        var expectedId = 0UL;
        var expectedPaymentPreimage =
            Convert.FromHexString("567cbdadb00b825448b2e414487d73a97f657f0634166d3ab3f3a2cc1042eda5");
        var stream = new MemoryStream(Convert.FromHexString(
                                          "00000000000000000000000000000000000000000000000000000000000000000000000000000000567CBDADB00B825448B2E414487D73A97F657F0634166D3AB3F3A2CC1042EDA5"));

        // Act
        var message = await _fulfillHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(expectedChannelId, message.Payload.ChannelId);
        Assert.Equal(expectedId, message.Payload.Id);
        Assert.Equal(expectedPaymentPreimage, message.Payload.PaymentPreimage);
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
        var paymentPreimage = Convert.FromHexString("567cbdadb00b825448b2e414487d73a97f657f0634166d3ab3f3a2cc1042eda5");
        var message = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(channelId, id, paymentPreimage));
        var stream = new MemoryStream();
        var expectedBytes =
            Convert.FromHexString(
                "00000000000000000000000000000000000000000000000000000000000000000000000000000000567CBDADB00B825448B2E414487D73A97F657F0634166D3AB3F3A2CC1042EDA5");

        // Act
        await _fulfillHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    #endregion

    #region attribution_data (TLV 1) and fulfillment_payload (TLV 3)

    [Fact]
    public async Task Given_MessageWithAttributionDataAndFulfillmentPayload_When_RoundTripping_Then_BothTlvsRoundTrip()
    {
        // Arrange
        var attributionData = Enumerable.Range(0, AttributionDataTlv.ValueLength).Select(i => (byte)(i * 3)).ToArray();
        var fulfillmentPayload = Enumerable.Range(0, 272).Select(i => (byte)i).ToArray();
        var message = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(ChannelId.Zero, 0,
                                                       Convert.FromHexString(
                                                           "567cbdadb00b825448b2e414487d73a97f657f0634166d3ab3f3a2cc1042eda5")),
                                                   new AttributionDataTlv(attributionData),
                                                   new FulfillmentPayloadTlv(fulfillmentPayload));
        var stream = new MemoryStream();

        // Act
        await _fulfillHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        var bytes = stream.ToArray();
        stream.Position = 0;
        var result = await _fulfillHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert: type 1 (920 = fd 0398) then type 3 (272 = fd 0110)
        Assert.Equal(PayloadHex + "01FD0398" + Convert.ToHexString(attributionData) + "03FD0110"
                   + Convert.ToHexString(fulfillmentPayload), Convert.ToHexString(bytes));
        Assert.NotNull(result.AttributionDataTlv);
        Assert.Equal(attributionData, result.AttributionDataTlv.AttributionData);
        Assert.NotNull(result.FulfillmentPayloadTlv);
        Assert.Equal(fulfillmentPayload, result.FulfillmentPayloadTlv.FulfillmentPayload);
        Assert.False(result.FulfillmentPayloadTlv.IsTooLong);
    }

    [Fact]
    public async Task Given_FulfillmentPayloadOver32KiB_When_DeserializeAsync_Then_ReadsItAndFlagsIsTooLong()
    {
        // Arrange: BOLT 2 wants the channel failed (not the connection), so the serializer keeps it for the handler
        var fulfillmentPayload = new byte[32769];
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "03FD8001" + Convert.ToHexString(fulfillmentPayload)));

        // Act
        var message = await _fulfillHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message.FulfillmentPayloadTlv);
        Assert.True(message.FulfillmentPayloadTlv.IsTooLong);
        Assert.Null(message.AttributionDataTlv);
    }

    [Fact]
    public async Task Given_AttributionDataOfWrongLength_When_DeserializeAsync_Then_Throws()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "0100"));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() =>
                                                                    _fulfillHtlcMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownEvenTlv_When_DeserializeAsync_Then_Throws()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "0401FF"));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() =>
                                                                    _fulfillHtlcMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    #endregion
}