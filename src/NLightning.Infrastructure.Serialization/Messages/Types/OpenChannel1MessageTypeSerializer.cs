using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Domain.Serialization.Interfaces;
using Exceptions;
using Interfaces;

public class OpenChannel1MessageTypeSerializer : IMessageTypeSerializer<OpenChannel1Message>
{
    /// <summary>
    /// The <c>open_channel_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.UpfrontShutdownScript, TlvConstants.ChannelType };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public OpenChannel1MessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                             ITlvConverterFactory tlvConverterFactory,
                                             ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not OpenChannel1Message openChannel1Message)
            throw new SerializationException($"Message is not of type {nameof(OpenChannel1Message)}");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(openChannel1Message.Extension, stream);
    }

    /// <summary>
    /// Deserialize an OpenChannel2Message from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized OpenChannel1Message.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing OpenChannel2Message</exception>
    public async Task<OpenChannel1Message> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<OpenChannel1Payload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension if available. A missing channel_type is not a wire error: BOLT 2 says to fail
            // the channel, which the channel-open validator does.
            if (stream.Position >= stream.Length)
                return new OpenChannel1Message(payload, null);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv = null;
            if (extension.TryGetTlv(TlvConstants.UpfrontShutdownScript, out var baseUpfrontShutdownTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<UpfrontShutdownScriptTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(UpfrontShutdownScriptTlv)}");
                upfrontShutdownScriptTlv = tlvConverter.ConvertFromBase(baseUpfrontShutdownTlv!);
            }

            ChannelTypeTlv? channelTypeTlv = null;
            if (extension.TryGetTlv(TlvConstants.ChannelType, out var baseChannelTypeTlv))
            {
                var channelTypeTlvConverter =
                    _tlvConverterFactory.GetConverter<ChannelTypeTlv>()
                 ?? throw new SerializationException($"No serializer found for tlv type {nameof(ChannelTypeTlv)}");
                channelTypeTlv = channelTypeTlvConverter.ConvertFromBase(baseChannelTypeTlv!);
            }

            return new OpenChannel1Message(payload, channelTypeTlv, upfrontShutdownScriptTlv);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException("Error deserializing OpenChannel1Message", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}