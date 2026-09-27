namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Exceptions;
using Helpers;
using Serialization.Messages.Types;

public class TxAddInputMessageTests
{
    private const string PlainHex =
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000100040001020300000000FFFFFFFD";

    // channel_id, serial_id = 2, prevtx_len = 0, prevtx_vout = 0, sequence
    private const string SharedPayloadHex =
        "0000000000000000000000000000000000000000000000000000000000000000" + "0000000000000002" + "0000" + "00000000"
      + "FFFFFFFD";

    private const string FundingTxIdHex = "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";

    private readonly TxAddInputMessageTypeSerializer _txAddInputMessageTypeSerializer;

    public TxAddInputMessageTests()
    {
        _txAddInputMessageTypeSerializer =
            new TxAddInputMessageTypeSerializer(SerializerHelper.PayloadSerializerFactory,
                                                SerializerHelper.TlvConverterFactory,
                                                SerializerHelper.TlvStreamSerializer);
    }

    [Fact]
    public async Task Given_ValidStream_When_DeserializeAsync_Then_ReturnsTxAddInputMessage()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        const ulong serialId = 1;
        byte[] prevTx = [0x00, 0x01, 0x02, 0x03];
        const uint prevTxVout = 0;
        const uint sequence = 0xFFFFFFFD;

        var stream = new MemoryStream(Convert.FromHexString(PlainHex));

        // Act
        var message = await _txAddInputMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Equal(channelId, message.Payload.ChannelId);
        Assert.Equal(serialId, message.Payload.SerialId);
        Assert.Equal(prevTx, message.Payload.PrevTx);
        Assert.Equal(prevTxVout, message.Payload.PrevTxVout);
        Assert.Equal(sequence, message.Payload.Sequence);
        Assert.Null(message.SharedInputTxIdTlv);
        Assert.Null(message.Extension);
    }

    [Fact]
    public async Task Given_GivenValidPayload_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        const ulong serialId = 1;
        byte[] prevTx = [0x00, 0x01, 0x02, 0x03];
        const uint prevTxVout = 0;
        const uint sequence = 0xFFFFFFFD;
        var message = new TxAddInputMessage(new TxAddInputPayload(channelId, serialId, prevTx, prevTxVout, sequence));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString(PlainHex);

        // Act
        await _txAddInputMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_SharedInputTxId_When_SerializeAsync_Then_WritesTlvTypeZeroWith32Bytes()
    {
        // Arrange
        var fundingTxId = Convert.FromHexString(FundingTxIdHex);
        var message = new TxAddInputMessage(new TxAddInputPayload(ChannelId.Zero, 2, [], 0, 0xFFFFFFFD),
                                            new SharedInputTxIdTlv(fundingTxId));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString(SharedPayloadHex + "0020" + FundingTxIdHex);

        // Act
        await _txAddInputMessageTypeSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(expectedBytes, stream.ToArray());
    }

    [Fact]
    public async Task Given_SharedInputTxIdTlv_When_DeserializeAsync_Then_ReadsTheFundingTxId()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(SharedPayloadHex + "0020" + FundingTxIdHex));

        // Act
        var message = await _txAddInputMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Empty(message.Payload.PrevTx);
        Assert.Equal(2UL, message.Payload.SerialId);
        Assert.NotNull(message.SharedInputTxIdTlv);
        Assert.Equal(Convert.FromHexString(FundingTxIdHex), (byte[])message.SharedInputTxIdTlv.FundingTxId);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_SharedInputTxId_When_RoundTripped_Then_MessageIsEqual()
    {
        // Arrange
        var original = new TxAddInputMessage(new TxAddInputPayload(ChannelId.Zero, 4, [], 1, 0xFFFFFFFD),
                                             new SharedInputTxIdTlv(Convert.FromHexString(FundingTxIdHex)));
        var stream = new MemoryStream();

        // Act
        await _txAddInputMessageTypeSerializer.SerializeAsync(original, stream);
        stream.Position = 0;
        var result = await _txAddInputMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(original.Payload.SerialId, result.Payload.SerialId);
        Assert.Equal(original.Payload.PrevTxVout, result.Payload.PrevTxVout);
        Assert.Equal(original.SharedInputTxIdTlv!.FundingTxId, result.SharedInputTxIdTlv!.FundingTxId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Given_SharedInputTxIdOfWrongLength_When_DeserializeAsync_Then_ThrowsMessageSerializationException(
        int length)
    {
        // Arrange
        var hex = SharedPayloadHex + "00" + length.ToString("X2") + string.Concat(Enumerable.Repeat("AB", length));
        var stream = new MemoryStream(Convert.FromHexString(hex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _txAddInputMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownEvenTlvAfterSharedInputTxId_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(SharedPayloadHex + "0020" + FundingTxIdHex + "02012A"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _txAddInputMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }
}