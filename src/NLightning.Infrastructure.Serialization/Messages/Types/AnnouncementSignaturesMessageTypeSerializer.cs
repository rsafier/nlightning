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
/// Serializer for <see cref="AnnouncementSignaturesMessage"/> (BOLT 7, type 259).
/// </summary>
/// <remarks>
/// The payload keeps every byte after <c>bitcoin_signature</c> verbatim
/// (<see cref="AnnouncementSignaturesPayload.ExtraData"/>), so parse → serialize is byte-identical. Those bytes are the
/// message's TLV extension (BOLT 1), for which no type is defined: they are read strictly here, so a malformed stream or
/// an even type fails the message (the peer gets a warning and is disconnected, NL-001/NL-207); odd types are ignored.
/// </remarks>
public class AnnouncementSignaturesMessageTypeSerializer : IMessageTypeSerializer<AnnouncementSignaturesMessage>
{
    /// <summary>
    /// The <c>announcement_signatures</c> TLV types this node understands (none are defined).
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes = new HashSet<BigSize>();

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public AnnouncementSignaturesMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                       ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not AnnouncementSignaturesMessage)
            throw new SerializationException($"Message is not of type {nameof(AnnouncementSignaturesMessage)}");

        var payloadSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                             ?? throw new SerializationException("No serializer found for payload type");
        await payloadSerializer.SerializeAsync(message.Payload, stream);
    }

    public async Task<AnnouncementSignaturesMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<AnnouncementSignaturesPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            if (!payload.ExtraData.IsEmpty)
            {
                using var extensionStream = new MemoryStream(payload.ExtraData.ToArray(), false);
                await _tlvStreamSerializer.DeserializeStrictAsync(extensionStream, s_knownExtensionTypes);
            }

            return new AnnouncementSignaturesMessage(payload);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(AnnouncementSignaturesMessage)}",
                                                    e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}