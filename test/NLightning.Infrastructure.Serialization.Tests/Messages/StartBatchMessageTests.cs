using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// BOLT 2 "Batching channel messages" wire (SP1-A-T1): <c>start_batch</c> (127) = <c>channel_id</c> ‖
/// <c>u16 batch_size</c> ‖ <c>start_batch_tlvs</c> (type 1 <c>message_type</c>, [<c>u16</c>]), read strictly with the
/// known set {1}.
/// </summary>
public class StartBatchMessageTests
{
    private const string ChannelIdHex = "0202020202020202020202020202020202020202020202020202020202020202";

    private static readonly ChannelId s_channelId = new(Convert.FromHexString(ChannelIdHex));

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer));

    private readonly IMessageTypeSerializer<StartBatchMessage> _serializer =
        SerializerHelper.MessageTypeSerializerFactory.GetSerializer<StartBatchMessage>()!;

    [Fact]
    public async Task Given_StartBatchOfCommitmentSigned_When_RoundTripped_Then_ItIsByteExact()
    {
        // Arrange: 127 = 0x007f; batch_size 3; TLV type 1, length 2, 132 = 0x0084
        var message = new StartBatchMessage(new StartBatchPayload(s_channelId, 3),
                                            StartBatchMessageTypeTlv.CommitmentSigned());

        // Act
        using var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, stream);
        var bytes = stream.ToArray();
        using var readStream = new MemoryStream(bytes);
        var parsed = Assert.IsType<StartBatchMessage>(await _messageSerializer.DeserializeMessageAsync(readStream));

        // Assert
        Assert.Equal("007F" + ChannelIdHex + "0003" + "01020084", Convert.ToHexString(bytes));
        Assert.Equal(MessageTypes.StartBatch, parsed.Type);
        Assert.Equal(s_channelId, parsed.Payload.ChannelId);
        Assert.Equal((ushort)3, parsed.Payload.BatchSize);
        Assert.NotNull(parsed.MessageTypeTlv);
        Assert.Equal((ushort)MessageTypes.CommitmentSigned, parsed.MessageTypeTlv.MessageType);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)21)]
    [InlineData(ushort.MaxValue)]
    public async Task Given_BatchSizeOutsideTheSenderRange_When_Deserialized_Then_ItIsParsedForTheReceiverRules(
        ushort batchSize)
    {
        // Arrange (the <= 1 and > 20 rules are the inbound loop's, SP-OP-04)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + batchSize.ToString("X4") + "01020084"));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(batchSize, message.Payload.BatchSize);
    }

    [Fact]
    public async Task Given_StartBatchWithoutMessageType_When_Deserialized_Then_TheTlvIsNull()
    {
        // Arrange (the receiver then ignores the start_batch and processes the messages one by one)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0002"));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.MessageTypeTlv);
        Assert.Null(message.Extension);
    }

    [Fact]
    public async Task Given_StartBatchWithOtherMessageType_When_Deserialized_Then_ItIsKept()
    {
        // Arrange (message_type 128 = update_add_htlc)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0002" + "01020080"));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal((ushort)128, message.MessageTypeTlv!.MessageType);
    }

    [Fact]
    public async Task Given_StartBatchWithUnknownEvenTlv_When_Deserialized_Then_ThrowsMessageSerializationException()
    {
        // Arrange (BOLT 1: an unknown even TLV type MUST fail the message; the known set is {1})
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0002" + "01020084" + "CA012A"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _serializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_StartBatchWithUnknownOddTlv_When_Deserialized_Then_ItIsIgnored()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0002" + "01020084" + "C9012A"));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal((ushort)132, message.MessageTypeTlv!.MessageType);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_StartBatchWithMalformedMessageType_When_Deserialized_Then_Throws()
    {
        // Arrange (message_type must be exactly a u16)
        var stream = new MemoryStream(Convert.FromHexString(ChannelIdHex + "0002" + "010184"));

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _serializer.DeserializeAsync(stream));
    }
}