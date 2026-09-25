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

public class UpdateAddHtlcMessageTypeSerializer : IMessageTypeSerializer<UpdateAddHtlcMessage>
{
    /// <summary>
    /// The <c>update_add_htlc_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.BlindedPath };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public UpdateAddHtlcMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                              ITlvConverterFactory tlvConverterFactory,
                                              ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not UpdateAddHtlcMessage updateAddHtlcMessage)
            throw new SerializationException("Message is not of type UpdateAddHtlcMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(updateAddHtlcMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize an UpdateAddHtlcMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized UpdateAddHtlcMessage.</returns>
    /// <remarks>
    /// The extension is read strictly: TLV types must be strictly increasing and unknown even types are rejected
    /// (BOLT 1). A malformed <c>blinded_path</c> point is also rejected.
    /// </remarks>
    /// <exception cref="MessageSerializationException">Error deserializing UpdateAddHtlcMessage</exception>
    public async Task<UpdateAddHtlcMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<UpdateAddHtlcPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension if available
            if (stream.Position >= stream.Length)
                return new UpdateAddHtlcMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.Any())
                return new UpdateAddHtlcMessage(payload);

            BlindedPathTlv? blindedPathTlv = null;
            if (extension.TryGetTlv(TlvConstants.BlindedPath, out var baseBlindedPathTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<BlindedPathTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(BlindedPathTlv)}");
                blindedPathTlv = tlvConverter.ConvertFromBase(baseBlindedPathTlv!);
            }

            return new UpdateAddHtlcMessage(payload, blindedPathTlv);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException or ArgumentException)
        {
            throw new MessageSerializationException("Error deserializing UpdateAddHtlcMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}