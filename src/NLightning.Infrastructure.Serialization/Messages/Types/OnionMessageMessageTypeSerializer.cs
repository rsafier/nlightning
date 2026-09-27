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
/// <c>onion_message</c> (type 513, BOLT 4 "Onion Messages"). It defines no TLV extension: a trailing stream is read
/// strictly, so an even type fails the message (BOLT 1) and odd ones are ignored.
/// </summary>
public class OnionMessageMessageTypeSerializer : IMessageTypeSerializer<OnionMessageMessage>
{
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes = new HashSet<BigSize>();

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public OnionMessageMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                             ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not OnionMessageMessage)
            throw new SerializationException($"Message is not of type {nameof(OnionMessageMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);
    }

    /// <exception cref="MessageSerializationException">Error deserializing OnionMessageMessage</exception>
    public async Task<OnionMessageMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<OnionMessagePayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            if (stream.Position < stream.Length)
                await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);

            return new OnionMessageMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(OnionMessageMessage)}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}