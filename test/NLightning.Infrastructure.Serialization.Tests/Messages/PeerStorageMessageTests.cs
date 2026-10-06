using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Node.PeerStorage;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// BOLT 1 <c>peer_storage</c> (7) and <c>peer_storage_retrieval</c> (9): <c>u16 length || blob</c>.
/// </summary>
public class PeerStorageMessageTests
{
    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_PeerStorage_When_Serialized_Then_WireIsTypeLengthAndBlob()
    {
        // Arrange
        var message = new PeerStorageMessage(new PeerStoragePayload(new byte[] { 0xAA, 0xBB, 0xCC }));

        // Act
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.Equal(Convert.FromHexString("00070003AABBCC"), bytes);
    }

    [Fact]
    public async Task Given_PeerStorageRetrieval_When_Serialized_Then_WireIsTypeLengthAndBlob()
    {
        // Arrange
        var message = new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(new byte[] { 0x01, 0x02 }));

        // Act
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.Equal(Convert.FromHexString("000900020102"), bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PeerStorageConstants.MaxBlobLength)]
    public async Task Given_PeerStorageOfAnyAllowedLength_When_RoundTripped_Then_BlobIsKept(int length)
    {
        // Arrange
        var blob = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var bytes = await SerializeAsync(new PeerStorageMessage(new PeerStoragePayload(blob)));

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(new MemoryStream(bytes));

        // Assert
        var peerStorage = Assert.IsType<PeerStorageMessage>(message);
        Assert.Equal(blob, peerStorage.Payload.Blob.ToArray());
    }

    [Fact]
    public async Task Given_PeerStorageRetrievalOfMaxLength_When_RoundTripped_Then_BlobIsKept()
    {
        // Arrange
        var blob = Enumerable.Range(0, PeerStorageConstants.MaxBlobLength).Select(i => (byte)(i * 7)).ToArray();
        var bytes = await SerializeAsync(new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(blob)));

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(new MemoryStream(bytes));

        // Assert
        var retrieval = Assert.IsType<PeerStorageRetrievalMessage>(message);
        Assert.Equal(blob, retrieval.Payload.Blob.ToArray());
    }

    [Fact]
    public void Given_BlobLongerThanBolt1Allows_When_PayloadIsBuilt_Then_Throws()
    {
        // Arrange
        var blob = new byte[PeerStorageConstants.MaxBlobLength + 1];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new PeerStoragePayload(blob));
        Assert.Throws<ArgumentException>(() => new PeerStorageRetrievalPayload(blob));
    }

    [Fact]
    public async Task Given_WireLengthAboveMax_When_Deserialized_Then_Throws()
    {
        // Arrange: length 65532 (0xFFFC)
        var bytes = new byte[4 + 0xFFFC];
        bytes[1] = 7;
        bytes[2] = 0xFF;
        bytes[3] = 0xFC;

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _messageSerializer.DeserializeMessageAsync(
                                                   new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Given_TruncatedBlob_When_Deserialized_Then_Throws()
    {
        // Arrange: says 4 bytes, carries 2
        var bytes = Convert.FromHexString("000900040102");

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _messageSerializer.DeserializeMessageAsync(
                                                   new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Given_UnknownOddTlvAfterBlob_When_Deserialized_Then_Ignored()
    {
        // Arrange: blob 0xAA, then TLV type 3 length 1
        var bytes = Convert.FromHexString("00070001AA030101");

        // Act
        var message = await _messageSerializer.DeserializeMessageAsync(new MemoryStream(bytes));

        // Assert
        var peerStorage = Assert.IsType<PeerStorageMessage>(message);
        Assert.Equal(new byte[] { 0xAA }, peerStorage.Payload.Blob.ToArray());
    }

    [Fact]
    public async Task Given_UnknownEvenTlvAfterBlob_When_Deserialized_Then_Rejected()
    {
        // Arrange: blob 0xAA, then TLV type 2 length 1 (BOLT 1: an unknown even type fails the message)
        var bytes = Convert.FromHexString("00070001AA020101");

        // Act & Assert
        await Assert.ThrowsAnyAsync<MessageSerializationException>(() => _messageSerializer.DeserializeMessageAsync(
                                                                        new MemoryStream(bytes)));
    }

    private async Task<byte[]> SerializeAsync(Domain.Protocol.Interfaces.IMessage message)
    {
        var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }
}