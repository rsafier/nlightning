using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;
using Interfaces;

/// <summary>
/// Serializer for <see cref="QueryShortChannelIdsMessage"/> (BOLT 7, type 261).
/// </summary>
public class QueryShortChannelIdsMessageTypeSerializer : IMessageTypeSerializer<QueryShortChannelIdsMessage>
{
    /// <summary>
    /// The <c>query_short_channel_ids_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail
    /// the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.QueryFlags };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public QueryShortChannelIdsMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                     ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not QueryShortChannelIdsMessage queryMessage)
            throw new SerializationException($"Message is not of type {nameof(QueryShortChannelIdsMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        await _tlvStreamSerializer.SerializeAsync(queryMessage.Extension, stream);
    }

    public async Task<QueryShortChannelIdsMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<QueryShortChannelIdsPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            if (stream.Position >= stream.Length)
                return new QueryShortChannelIdsMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            extension.TryGetTlv(TlvConstants.QueryFlags, out var queryFlagsTlv);

            return new QueryShortChannelIdsMessage(payload, queryFlagsTlv);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(QueryShortChannelIdsMessage)}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}