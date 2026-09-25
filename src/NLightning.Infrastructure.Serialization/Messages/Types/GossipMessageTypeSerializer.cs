using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Serializer for the BOLT 7 gossip messages, which are kept as raw bytes (see <see cref="GossipMessage"/>).
/// </summary>
/// <typeparam name="TMessage">The concrete gossip message type.</typeparam>
public class GossipMessageTypeSerializer<TMessage> : IMessageTypeSerializer<TMessage> where TMessage : GossipMessage
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly Func<GossipPayload, TMessage> _messageFactory;

    public GossipMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                       Func<GossipPayload, TMessage> messageFactory)
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
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<GossipPayload>()
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