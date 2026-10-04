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

public class TxCompleteMessageTypeSerializer : IMessageTypeSerializer<TxCompleteMessage>
{
    /// <summary>
    /// The <c>tx_complete_tlvs</c> types this node understands (BOLTs PR #1324 <c>commit_nonces</c> and <c>funding_nonce</c>). BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TaprootTlvConstants.CommitNonces, TaprootTlvConstants.FundingNonce };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public TxCompleteMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                           ITlvConverterFactory tlvConverterFactory,
                                           ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not TxCompleteMessage)
            throw new SerializationException($"Message is not of type {nameof(TxCompleteMessage)}");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(message.Extension, stream);
    }

    /// <summary>
    /// Deserialize a TxCompleteMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized TxCompleteMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing TxCompleteMessage</exception>
    public async Task<TxCompleteMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<TxCompletePayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension
            if (stream.Position >= stream.Length)
                return new TxCompleteMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            var commitNoncesTlv = extension.ReadTlv<CommitNoncesTlv>(TaprootTlvConstants.CommitNonces, _tlvConverterFactory);
            var fundingNonceTlv = extension.ReadTlv<FundingNonceTlv>(TaprootTlvConstants.FundingNonce, _tlvConverterFactory);
            return new TxCompleteMessage(payload, commitNoncesTlv, fundingNonceTlv);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException)
        {
            throw new MessageSerializationException("Error deserializing TxCompleteMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}