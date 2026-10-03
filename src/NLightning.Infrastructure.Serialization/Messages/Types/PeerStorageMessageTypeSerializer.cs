using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;
using Interfaces;

/// <summary>
/// <c>peer_storage</c> (type 7, BOLT 1). It defines no TLV: a trailing stream is read strictly, so an even type fails
/// the message (BOLT 1) and odd ones are ignored.
/// </summary>
public class PeerStorageMessageTypeSerializer : IMessageTypeSerializer<PeerStorageMessage>
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public PeerStorageMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                            ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not PeerStorageMessage)
            throw new SerializationException($"Message is not of type {nameof(PeerStorageMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);
    }

    /// <exception cref="MessageSerializationException">Error deserializing PeerStorageMessage</exception>
    public async Task<PeerStorageMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<PeerStoragePayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            await PeerStorageExtension.SkipAsync(_tlvStreamSerializer, stream);

            return new PeerStorageMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(PeerStorageMessage)}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}

/// <summary>
/// <c>peer_storage_retrieval</c> (type 9, BOLT 1). It defines no TLV: a trailing stream is read strictly, so an even
/// type fails the message (BOLT 1) and odd ones are ignored.
/// </summary>
public class PeerStorageRetrievalMessageTypeSerializer : IMessageTypeSerializer<PeerStorageRetrievalMessage>
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public PeerStorageRetrievalMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                     ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not PeerStorageRetrievalMessage)
            throw new SerializationException($"Message is not of type {nameof(PeerStorageRetrievalMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);
    }

    /// <exception cref="MessageSerializationException">Error deserializing PeerStorageRetrievalMessage</exception>
    public async Task<PeerStorageRetrievalMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<PeerStorageRetrievalPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            await PeerStorageExtension.SkipAsync(_tlvStreamSerializer, stream);

            return new PeerStorageRetrievalMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(PeerStorageRetrievalMessage)}",
                                                    e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}

internal static class PeerStorageExtension
{
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes = new HashSet<BigSize>();

    public static async Task SkipAsync(ITlvStreamSerializer tlvStreamSerializer, Stream stream)
    {
        if (stream.Position < stream.Length)
            await tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
    }
}