namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Exceptions;
using Helpers;

public class TxAddInputMessageTests
{
    private const string PlainHex =
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000100040001020300000000FFFFFFFD";

    // channel_id, serial_id = 2, prevtx_len = 0, prevtx_vout = 0, sequence
    private const string SharedPayloadHex =
        "0000000000000000000000000000000000000000000000000000000000000000" + "0000000000000002" + "0000" + "00000000"
      + "FFFFFFFD";

    private const string FundingTxIdHex = "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";

    private readonly IMessageTypeSerializer<TxAddInputMessage> _txAddInputMessageTypeSerializer;

    public TxAddInputMessageTests()
    {
        _txAddInputMessageTypeSerializer =
            SerializerHelper.MessageTypeSerializerFactory.GetSerializer<TxAddInputMessage>()!;
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
        var stream = new MemoryStream(Convert.FromHexString(SharedPayloadHex + "0020" + FundingTxIdHex + "04012A"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _txAddInputMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    // NL-957: prevtx_details (BOLTs PR #1324 type 2, Eclair 0.14.3 type 1111): txid || u64 amount || P2TR script
    private const string DetailsTxIdHex = "A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBFC0";
    private const string DetailsAmountHex = "00000000000186A0"; // 100,000 sat
    private const string DetailsScriptHex = "5120" + "5555555555555555555555555555555555555555555555555555555555555555";
    private const string DetailsValueHex = DetailsTxIdHex + DetailsAmountHex + DetailsScriptHex;

    // channel_id, serial_id = 1, prevtx_len = 0, prevtx_vout = 2, sequence
    private const string DetailsPayloadHex =
        "0000000000000000000000000000000000000000000000000000000000000000" + "0000000000000001" + "0000" + "00000002"
      + "FFFFFFFD";

    [Fact]
    public async Task Given_PrevTxDetails_When_SerializeAsync_Then_WritesTlvTypeTwo()
    {
        // Arrange
        var message = new TxAddInputMessage(new TxAddInputPayload(ChannelId.Zero, 1, [], 2, 0xFFFFFFFD), null,
                                            new PrevTxDetailsTlv(Convert.FromHexString(DetailsTxIdHex), 100_000,
                                                                 Convert.FromHexString(DetailsScriptHex)));
        var stream = new MemoryStream();

        // Act
        await _txAddInputMessageTypeSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(Convert.FromHexString(DetailsPayloadHex + "024A" + DetailsValueHex), stream.ToArray());
    }

    [Theory]
    [InlineData("024A", 2UL)]
    [InlineData("FD04574A", 1111UL)]
    public async Task Given_PrevTxDetailsOfEitherType_When_DeserializeAsync_Then_ReadsTheSpentOutput(
        string typeAndLengthHex, ulong expectedType)
    {
        // Arrange: type 1111 is how Eclair 0.14.3 writes TxAddInputTlv.PrevTxOut (BigSize 0xFD0457)
        var stream = new MemoryStream(Convert.FromHexString(DetailsPayloadHex + typeAndLengthHex + DetailsValueHex));

        // Act
        var message = await _txAddInputMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Empty(message.Payload.PrevTx);
        Assert.Equal(2u, message.Payload.PrevTxVout);
        Assert.Null(message.SharedInputTxIdTlv);
        var details = Assert.IsType<PrevTxDetailsTlv>(message.PrevTxDetailsTlv);
        Assert.Equal(expectedType, (ulong)details.Type);
        Assert.Equal(Convert.FromHexString(DetailsTxIdHex), (byte[])details.PrevTxId);
        Assert.Equal(100_000UL, details.AmountSatoshis);
        Assert.Equal(Convert.FromHexString(DetailsScriptHex), (byte[])details.ScriptPubKey);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_BothPrevTxDetailsTypes_When_DeserializeAsync_Then_TheSpecTypeWins()
    {
        // Arrange: type 2 carries 100,000 sat, type 1111 a different amount
        var eclairValueHex = DetailsTxIdHex + "0000000000000001" + DetailsScriptHex;
        var stream = new MemoryStream(Convert.FromHexString(DetailsPayloadHex + "024A" + DetailsValueHex + "FD04574A"
                                                          + eclairValueHex));

        // Act
        var message = await _txAddInputMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(2UL, (ulong)message.PrevTxDetailsTlv!.Type);
        Assert.Equal(100_000UL, message.PrevTxDetailsTlv.AmountSatoshis);
    }

    [Fact]
    public async Task Given_PrevTxDetails_When_RoundTripped_Then_MessageIsEqual()
    {
        // Arrange
        var original = new TxAddInputMessage(new TxAddInputPayload(ChannelId.Zero, 3, [], 5, 0xFFFFFFFD), null,
                                             new PrevTxDetailsTlv(Convert.FromHexString(DetailsTxIdHex), 42_000,
                                                                  Convert.FromHexString(DetailsScriptHex)));
        var stream = new MemoryStream();

        // Act
        await _txAddInputMessageTypeSerializer.SerializeAsync(original, stream);
        stream.Position = 0;
        var result = await _txAddInputMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(original.PrevTxDetailsTlv!.PrevTxId, result.PrevTxDetailsTlv!.PrevTxId);
        Assert.Equal(42_000UL, result.PrevTxDetailsTlv.AmountSatoshis);
        Assert.Equal(original.PrevTxDetailsTlv.ScriptPubKey, result.PrevTxDetailsTlv.ScriptPubKey);
    }

    [Fact]
    public async Task Given_ShortPrevTxDetails_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange: 39 bytes cannot hold a txid and an amount
        var stream = new MemoryStream(Convert.FromHexString(DetailsPayloadHex + "0227"
                                                          + string.Concat(Enumerable.Repeat("AB", 39))));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _txAddInputMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }
}