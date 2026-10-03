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

public class CommitmentSignedMessageTypeSerializer : IMessageTypeSerializer<CommitmentSignedMessage>
{
    /// <summary>
    /// The <c>commitment_signed_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.FundingTxId };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public CommitmentSignedMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                 ITlvConverterFactory tlvConverterFactory,
                                                 ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not CommitmentSignedMessage commitmentSignedMessage)
            throw new SerializationException("Message is not of type CommitmentSignedMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(commitmentSignedMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize a CommitmentSignedMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized CommitmentSignedMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing CommitmentSignedMessage</exception>
    public async Task<CommitmentSignedMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<CommitmentSignedPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension. funding_txid is optional on receipt (older peers do not send it).
            if (stream.Position >= stream.Length)
                return new CommitmentSignedMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.TryGetTlv(TlvConstants.FundingTxId, out var baseFundingTxIdTlv))
                return new CommitmentSignedMessage(payload);

            var tlvConverter = _tlvConverterFactory.GetConverter<FundingTxIdTlv>()
                            ?? throw new SerializationException(
                                   $"No serializer found for tlv type {nameof(FundingTxIdTlv)}");
            var fundingTxIdTlv = tlvConverter.ConvertFromBase(baseFundingTxIdTlv!);

            return new CommitmentSignedMessage(payload, fundingTxIdTlv);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException)
        {
            throw new MessageSerializationException("Error deserializing CommitmentSignedMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}