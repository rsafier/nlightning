using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Serializer for <see cref="ReplyShortChannelIdsEndMessage"/> (BOLT 7, type 262).
/// </summary>
public class ReplyShortChannelIdsEndMessageTypeSerializer : IMessageTypeSerializer<ReplyShortChannelIdsEndMessage>
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;

    public ReplyShortChannelIdsEndMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not ReplyShortChannelIdsEndMessage)
            throw new SerializationException($"Message is not of type {nameof(ReplyShortChannelIdsEndMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);
    }

    public async Task<ReplyShortChannelIdsEndMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<ReplyShortChannelIdsEndPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            return new ReplyShortChannelIdsEndMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(ReplyShortChannelIdsEndMessage)}",
                                                    e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}