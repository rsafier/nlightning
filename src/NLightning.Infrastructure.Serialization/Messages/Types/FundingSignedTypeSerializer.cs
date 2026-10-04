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

public class FundingSignedMessageTypeSerializer : IMessageTypeSerializer<FundingSignedMessage>
{
    /// <summary>
    /// The <c>funding_signed_tlvs</c> types this node understands (simple taproot <c>partial_signature_with_nonce</c>). BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TaprootTlvConstants.PartialSignatureWithNonce };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public FundingSignedMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                              ITlvConverterFactory tlvConverterFactory,
                                              ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not FundingSignedMessage)
            throw new SerializationException($"Message is not of type {nameof(FundingSignedMessage)}");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(message.Extension, stream);
    }

    /// <summary>
    /// Deserialize a FundingSignedMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized FundingSignedMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing FundingSignedMessage</exception>
    public async Task<FundingSignedMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<FundingSignedPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension
            if (stream.Position >= stream.Length)
                return new FundingSignedMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            var partialSignatureWithNonceTlv = extension.ReadTlv<PartialSignatureWithNonceTlv>(TaprootTlvConstants.PartialSignatureWithNonce, _tlvConverterFactory);
            return new FundingSignedMessage(payload, partialSignatureWithNonceTlv);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException)
        {
            throw new MessageSerializationException("Error deserializing FundingSignedMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}