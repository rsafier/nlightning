using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Exceptions;
using Helpers;

public class TxSignaturesMessageTests
{
    private const string PlainHex =
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010004FFFFFFFD";

    private const string SignatureHex =
        "4737AF4C6314905296FD31D3610BD638F92C8A3687D0C6D845E3B9EF4957670733A30A9A81F924CD9F73F46805D0FB60D7C293FB2D8100DD3FA92B10934A7320";

    private readonly IMessageTypeSerializer<TxSignaturesMessage> _txSignaturesMessageTypeSerializer;

    public TxSignaturesMessageTests()
    {
        _txSignaturesMessageTypeSerializer =
            SerializerHelper.MessageTypeSerializerFactory.GetSerializer<TxSignaturesMessage>()!;
    }

    [Fact]
    public async Task Given_ValidStream_When_DeserializeAsync_Then_ReturnsTxSignaturesMessage()
    {
        // Arrange
        var expectedChannelId = ChannelId.Zero;
        byte[] expectedTxId = ChannelId.Zero; // 32 zeros
        var expectedWitnesses = new List<Witness>
        {
            new([0xFF, 0xFF, 0xFF, 0xFD])
        };
        var stream = new MemoryStream(Convert.FromHexString(PlainHex));

        // Act
        var message = await _txSignaturesMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Equal(expectedChannelId, message.Payload.ChannelId);
        Assert.Equal(expectedTxId, message.Payload.TxId);
        Assert.Equal(expectedWitnesses.Count, message.Payload.Witnesses.Count);
        Assert.Equal(expectedWitnesses[0], message.Payload.Witnesses[0]);
        Assert.Null(message.SharedInputSignatureTlv);
    }

    [Fact]
    public async Task Given_GivenValidPayload_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        byte[] txId = ChannelId.Zero; // 32 zeros
        var witnesses = new List<Witness>
        {
            new([0xFF, 0xFF, 0xFF, 0xFD])
        };
        var message = new TxSignaturesMessage(new TxSignaturesPayload(channelId, txId, witnesses));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString(PlainHex);

        // Act
        await _txSignaturesMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_SharedInputSignature_When_SerializeAsync_Then_WritesTlvTypeZeroWith64Bytes()
    {
        // Arrange
        byte[] txId = ChannelId.Zero;
        var message = new TxSignaturesMessage(new TxSignaturesPayload(ChannelId.Zero, txId, [new([0xFF, 0xFF, 0xFF, 0xFD])]),
                                              new SharedInputSignatureTlv(Convert.FromHexString(SignatureHex)));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString(PlainHex + "0040" + SignatureHex);

        // Act
        await _txSignaturesMessageTypeSerializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(expectedBytes, stream.ToArray());
    }

    [Fact]
    public async Task Given_SharedInputSignatureTlv_When_DeserializeAsync_Then_ReadsTheSignature()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PlainHex + "0040" + SignatureHex));

        // Act
        var message = await _txSignaturesMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Single(message.Payload.Witnesses);
        Assert.NotNull(message.SharedInputSignatureTlv);
        Assert.Equal(Convert.FromHexString(SignatureHex), (byte[])message.SharedInputSignatureTlv.Signature);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_SharedInputSignature_When_RoundTripped_Then_MessageIsEqual()
    {
        // Arrange
        var txId = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var original = new TxSignaturesMessage(new TxSignaturesPayload(ChannelId.Zero, txId, []),
                                               new SharedInputSignatureTlv(Convert.FromHexString(SignatureHex)));
        var stream = new MemoryStream();

        // Act
        await _txSignaturesMessageTypeSerializer.SerializeAsync(original, stream);
        stream.Position = 0;
        var result = await _txSignaturesMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(txId, result.Payload.TxId);
        Assert.Empty(result.Payload.Witnesses);
        Assert.Equal(original.SharedInputSignatureTlv!.Signature, result.SharedInputSignatureTlv!.Signature);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public async Task Given_SharedInputSignatureOfWrongLength_When_DeserializeAsync_Then_ThrowsMessageSerializationException(
        int length)
    {
        // Arrange
        var hex = PlainHex + "00" + length.ToString("X2") + string.Concat(Enumerable.Repeat("AB", length));
        var stream = new MemoryStream(Convert.FromHexString(hex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _txSignaturesMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownEvenTlvAfterSharedInputSignature_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PlainHex + "0040" + SignatureHex + "02012A"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _txSignaturesMessageTypeSerializer
                                                                       .DeserializeAsync(stream));
    }
}