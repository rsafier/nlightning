using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Serializer for <see cref="ChannelUpdateMessage"/> (BOLT 7, type 258). The message has no TLV extension: bytes after
/// the known fields are unknown fields of the payload, kept in <see cref="ChannelUpdatePayload.ExtraData"/>.
/// </summary>
public class ChannelUpdateMessageTypeSerializer : IMessageTypeSerializer<ChannelUpdateMessage>
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;

    public ChannelUpdateMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not ChannelUpdateMessage)
            throw new SerializationException($"Message is not of type {nameof(ChannelUpdateMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);
    }

    public async Task<ChannelUpdateMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<ChannelUpdatePayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            return new ChannelUpdateMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(ChannelUpdateMessage)}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}