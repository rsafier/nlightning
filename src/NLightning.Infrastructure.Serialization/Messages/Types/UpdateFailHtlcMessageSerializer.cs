using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Exceptions;
using Interfaces;

public class UpdateFailHtlcMessageTypeSerializer : IMessageTypeSerializer<UpdateFailHtlcMessage>
{
    /// <summary>
    /// The <c>update_fail_htlc_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.AttributionData };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public UpdateFailHtlcMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                               ITlvConverterFactory tlvConverterFactory,
                                               ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not UpdateFailHtlcMessage updateFailHtlcMessage)
            throw new SerializationException("Message is not of type UpdateFailHtlcMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(updateFailHtlcMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize an UpdateFailHtlcMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized UpdateFailHtlcMessage.</returns>
    /// <remarks>
    /// The extension is read strictly: TLV types must be strictly increasing and unknown even types are rejected
    /// (BOLT 1). An <c>attribution_data</c> that is not exactly 920 bytes is rejected too (BOLT 1: a known
    /// fixed-size type with another length MUST fail the stream).
    /// </remarks>
    /// <exception cref="MessageSerializationException">Error deserializing UpdateFailHtlcMessage</exception>
    public async Task<UpdateFailHtlcMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<UpdateFailHtlcPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension if available
            if (stream.Position >= stream.Length)
                return new UpdateFailHtlcMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.Any())
                return new UpdateFailHtlcMessage(payload);

            AttributionDataTlv? attributionDataTlv = null;
            if (extension.TryGetTlv(TlvConstants.AttributionData, out var baseAttributionDataTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<AttributionDataTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(AttributionDataTlv)}");
                attributionDataTlv = tlvConverter.ConvertFromBase(baseAttributionDataTlv!);
            }

            return new UpdateFailHtlcMessage(payload, attributionDataTlv);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException or ArgumentException)
        {
            throw new MessageSerializationException("Error deserializing UpdateFailHtlcMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}