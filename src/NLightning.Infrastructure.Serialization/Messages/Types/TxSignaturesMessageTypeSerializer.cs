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

public class TxSignaturesMessageTypeSerializer : IMessageTypeSerializer<TxSignaturesMessage>
{
    /// <summary>
    /// The <c>tx_signatures_tlvs</c> types this node understands (BOLT 2: type 0 <c>shared_input_signature</c>). BOLT 1:
    /// an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize>
        {
            InteractiveTxTlvConstants.SharedInputSignature, TaprootTlvConstants.SharedInputPartialSignature
        };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public TxSignaturesMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                             ITlvConverterFactory tlvConverterFactory,
                                             ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not TxSignaturesMessage txSignaturesMessage)
            throw new SerializationException("Message is not of type TxSignaturesMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(txSignaturesMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize a TxSignaturesMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized TxSignaturesMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing TxSignaturesMessage</exception>
    public async Task<TxSignaturesMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<TxSignaturesPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension
            if (stream.Position >= stream.Length)
                return new TxSignaturesMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.Any())
                return new TxSignaturesMessage(payload);

            SharedInputSignatureTlv? sharedInputSignatureTlv = null;
            if (extension.TryGetTlv(InteractiveTxTlvConstants.SharedInputSignature, out var baseSharedInputSignature))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<SharedInputSignatureTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(SharedInputSignatureTlv)}");
                sharedInputSignatureTlv = tlvConverter.ConvertFromBase(baseSharedInputSignature!);
            }

            var sharedInputPartialSignatureTlv =
                extension.ReadTlv<SharedInputPartialSignatureTlv>(TaprootTlvConstants.SharedInputPartialSignature,
                                                                  _tlvConverterFactory);

            return new TxSignaturesMessage(payload, sharedInputSignatureTlv, sharedInputPartialSignatureTlv);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException("Error deserializing TxSignaturesMessage", e);
        }
        catch (InvalidCastException e)
        {
            throw new MessageSerializationException("Error deserializing TxSignaturesMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}