using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Serializer for <see cref="GossipTimestampFilterMessage"/> (BOLT 7, type 265).
/// </summary>
public class GossipTimestampFilterMessageTypeSerializer : IMessageTypeSerializer<GossipTimestampFilterMessage>
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;

    public GossipTimestampFilterMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not GossipTimestampFilterMessage)
            throw new SerializationException($"Message is not of type {nameof(GossipTimestampFilterMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);
    }

    public async Task<GossipTimestampFilterMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<GossipTimestampFilterPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            return new GossipTimestampFilterMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(GossipTimestampFilterMessage)}",
                                                    e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}