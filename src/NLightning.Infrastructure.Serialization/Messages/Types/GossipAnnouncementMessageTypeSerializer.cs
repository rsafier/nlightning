using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Exceptions;

/// <summary>
/// Serializer for the BOLT 7 announcements <see cref="ChannelAnnouncementMessage"/> (256) and
/// <see cref="NodeAnnouncementMessage"/> (257). Neither has a TLV extension: bytes after the known fields are unknown
/// fields of the payload (covered by the signatures), kept by the Domain codec.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <typeparam name="TPayload">The payload type.</typeparam>
public class GossipAnnouncementMessageTypeSerializer<TMessage, TPayload> : IMessageTypeSerializer<TMessage>
    where TMessage : class, IMessage
    where TPayload : class, IMessagePayload
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly Func<TPayload, TMessage> _messageFactory;

    public GossipAnnouncementMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                   Func<TPayload, TMessage> messageFactory)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _messageFactory = messageFactory;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not TMessage)
            throw new SerializationException($"Message is not of type {typeof(TMessage).Name}");

        var payloadSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                             ?? throw new SerializationException("No serializer found for payload type");
        await payloadSerializer.SerializeAsync(message.Payload, stream);
    }

    public async Task<TMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<TPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            return _messageFactory(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {typeof(TMessage).Name}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}