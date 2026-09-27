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

/// <summary>
/// Serializes <c>splice_init</c> (type 80, SP-W-01); <c>splice_init_tlvs</c> is read strictly with the known set {2}.
/// </summary>
public class SpliceInitMessageTypeSerializer : IMessageTypeSerializer<SpliceInitMessage>
{
    /// <summary>
    /// The <c>splice_init_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.RequireConfirmedInputs };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public SpliceInitMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                           ITlvConverterFactory tlvConverterFactory,
                                           ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not SpliceInitMessage spliceInitMessage)
            throw new SerializationException($"Message is not of type {nameof(SpliceInitMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        await _tlvStreamSerializer.SerializeAsync(spliceInitMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize a SpliceInitMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized SpliceInitMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing SpliceInitMessage</exception>
    public async Task<SpliceInitMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<SpliceInitPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            if (stream.Position >= stream.Length)
                return new SpliceInitMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.TryGetTlv(TlvConstants.RequireConfirmedInputs, out var baseRequireConfirmedInputsTlv))
                return new SpliceInitMessage(payload);

            var tlvConverter = _tlvConverterFactory.GetConverter<RequireConfirmedInputsTlv>()
                            ?? throw new SerializationException(
                                   $"No serializer found for tlv type {nameof(RequireConfirmedInputsTlv)}");

            return new SpliceInitMessage(payload, tlvConverter.ConvertFromBase(baseRequireConfirmedInputsTlv!));
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(SpliceInitMessage)}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}