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

public class TxAddInputMessageTypeSerializer : IMessageTypeSerializer<TxAddInputMessage>
{
    /// <summary>
    /// The <c>tx_add_input_tlvs</c> types this node understands (BOLT 2: type 0 <c>shared_input_txid</c>; BOLTs PR #1324:
    /// type 2 <c>prevtx_details</c>, and Eclair 0.14.3's odd 1111 for the same record, NL-957). BOLT 1: an unknown even
    /// type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize>
        {
            InteractiveTxTlvConstants.SharedInputTxId,
            InteractiveTxTlvConstants.PrevTxDetails,
            InteractiveTxTlvConstants.PrevTxDetailsEclair
        };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public TxAddInputMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                           ITlvConverterFactory tlvConverterFactory,
                                           ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not TxAddInputMessage txAddInputMessage)
            throw new SerializationException("Message is not of type TxAddInputMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(txAddInputMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize a TxAddInputMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized TxAddInputMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing TxAddInputMessage</exception>
    public async Task<TxAddInputMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<TxAddInputPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension
            if (stream.Position >= stream.Length)
                return new TxAddInputMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.Any())
                return new TxAddInputMessage(payload);

            SharedInputTxIdTlv? sharedInputTxIdTlv = null;
            if (extension.TryGetTlv(InteractiveTxTlvConstants.SharedInputTxId, out var baseSharedInputTxId))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<SharedInputTxIdTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(SharedInputTxIdTlv)}");
                sharedInputTxIdTlv = tlvConverter.ConvertFromBase(baseSharedInputTxId!);
            }

            // prevtx_details: the spec's type 2 wins over Eclair's prototype 1111 when a peer sends both
            PrevTxDetailsTlv? prevTxDetailsTlv = null;
            if (extension.TryGetTlv(InteractiveTxTlvConstants.PrevTxDetails, out var basePrevTxDetails)
             || extension.TryGetTlv(InteractiveTxTlvConstants.PrevTxDetailsEclair, out basePrevTxDetails))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<PrevTxDetailsTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(PrevTxDetailsTlv)}");
                prevTxDetailsTlv = tlvConverter.ConvertFromBase(basePrevTxDetails!);
            }

            return new TxAddInputMessage(payload, sharedInputTxIdTlv, prevTxDetailsTlv);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException("Error deserializing TxAddInputMessage", e);
        }
        catch (InvalidCastException e)
        {
            throw new MessageSerializationException("Error deserializing TxAddInputMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}