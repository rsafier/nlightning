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

public class UpdateFulfillHtlcMessageTypeSerializer : IMessageTypeSerializer<UpdateFulfillHtlcMessage>
{
    /// <summary>
    /// The <c>update_fulfill_htlc_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the
    /// stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.AttributionData, TlvConstants.FulfillmentPayload };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public UpdateFulfillHtlcMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                  ITlvConverterFactory tlvConverterFactory,
                                                  ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not UpdateFulfillHtlcMessage updateFulfillHtlcMessage)
            throw new SerializationException("Message is not of type UpdateFulfillHtlcMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(updateFulfillHtlcMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize a UpdateFulfillHtlcMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized UpdateFulfillHtlcMessage.</returns>
    /// <remarks>
    /// The extension is read strictly: TLV types must be strictly increasing and unknown even types are rejected
    /// (BOLT 1). An <c>attribution_data</c> that is not exactly 920 bytes is rejected. A <c>fulfillment_payload</c>
    /// longer than 32768 bytes is accepted here, because BOLT 2 wants the channel failed rather than the connection
    /// closed; the handler checks <see cref="FulfillmentPayloadTlv.IsTooLong"/>.
    /// </remarks>
    /// <exception cref="MessageSerializationException">Error deserializing UpdateFulfillHtlcMessage</exception>
    public async Task<UpdateFulfillHtlcMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<UpdateFulfillHtlcPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension if available
            if (stream.Position >= stream.Length)
                return new UpdateFulfillHtlcMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.Any())
                return new UpdateFulfillHtlcMessage(payload);

            AttributionDataTlv? attributionDataTlv = null;
            if (extension.TryGetTlv(TlvConstants.AttributionData, out var baseAttributionDataTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<AttributionDataTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(AttributionDataTlv)}");
                attributionDataTlv = tlvConverter.ConvertFromBase(baseAttributionDataTlv!);
            }

            FulfillmentPayloadTlv? fulfillmentPayloadTlv = null;
            if (extension.TryGetTlv(TlvConstants.FulfillmentPayload, out var baseFulfillmentPayloadTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<FulfillmentPayloadTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(FulfillmentPayloadTlv)}");
                fulfillmentPayloadTlv = tlvConverter.ConvertFromBase(baseFulfillmentPayloadTlv!);
            }

            return new UpdateFulfillHtlcMessage(payload, attributionDataTlv, fulfillmentPayloadTlv);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException or ArgumentException)
        {
            throw new MessageSerializationException("Error deserializing UpdateFulfillHtlcMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}