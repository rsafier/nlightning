using System.Buffers.Binary;
using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Node.PeerStorage;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// The <c>u16</c> length and blob of <c>peer_storage</c> (BOLT 1).
/// </summary>
public class PeerStoragePayloadSerializer : IPayloadSerializer<PeerStoragePayload>
{
    public Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not PeerStoragePayload peerStoragePayload)
            throw new SerializationException($"Payload is not of type {nameof(PeerStoragePayload)}");

        return PeerStorageBlobCodec.WriteAsync(peerStoragePayload, stream);
    }

    public async Task<PeerStoragePayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            return new PeerStoragePayload(await PeerStorageBlobCodec.ReadAsync(stream));
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(PeerStoragePayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}

/// <summary>
/// The <c>u16</c> length and blob of <c>peer_storage_retrieval</c> (BOLT 1).
/// </summary>
public class PeerStorageRetrievalPayloadSerializer : IPayloadSerializer<PeerStorageRetrievalPayload>
{
    public Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not PeerStorageRetrievalPayload retrievalPayload)
            throw new SerializationException($"Payload is not of type {nameof(PeerStorageRetrievalPayload)}");

        return PeerStorageBlobCodec.WriteAsync(retrievalPayload, stream);
    }

    public async Task<PeerStorageRetrievalPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            return new PeerStorageRetrievalPayload(await PeerStorageBlobCodec.ReadAsync(stream));
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(PeerStorageRetrievalPayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}

internal static class PeerStorageBlobCodec
{
    public static async Task WriteAsync(PeerStorageBlobPayload payload, Stream stream)
    {
        var length = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)payload.Blob.Length);
        await stream.WriteAsync(length);
        await stream.WriteAsync(payload.Blob);
    }

    /// <exception cref="SerializationException">The blob is truncated or longer than BOLT 1 allows.</exception>
    public static async Task<byte[]> ReadAsync(Stream stream)
    {
        var lengthBytes = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);

        if (length > PeerStorageConstants.MaxBlobLength)
            throw new SerializationException(
                $"Peer storage blob of {length} bytes is longer than {PeerStorageConstants.MaxBlobLength}");
        if (stream.Length - stream.Position < length)
            throw new SerializationException($"Peer storage blob is truncated: expected {length} bytes");

        var blob = new byte[length];
        await stream.ReadExactlyAsync(blob);
        return blob;
    }
}