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

public class InitMessageTypeSerializer : IMessageTypeSerializer<InitMessage>
{
    /// <summary>
    /// The <c>init_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.Networks, TlvConstants.RemoteAddress };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public InitMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                     ITlvConverterFactory tlvConverterFactory, ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not InitMessage initMessage)
            throw new SerializationException("Message is not of type InitMessage");

        // Get the payload serializer
        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        // Serialize the TLV stream
        await _tlvStreamSerializer.SerializeAsync(initMessage.Extension, stream);
    }

    /// <summary>
    /// Deserialize an InitMessage from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized InitMessage.</returns>
    /// <exception cref="MessageSerializationException">Error deserializing InitMessage</exception>
    public async Task<InitMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            // Deserialize payload
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<InitPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            // Deserialize extension if available
            if (stream.Position >= stream.Length)
                return new InitMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            if (!extension.Any())
                return new InitMessage(payload);

            NetworksTlv? networksTlv = null;
            if (extension.TryGetTlv(TlvConstants.Networks, out var baseNetworkTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<NetworksTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(NetworksTlv)}");
                networksTlv = tlvConverter.ConvertFromBase(baseNetworkTlv!);
            }

            RemoteAddressTlv? remoteAddressTlv = null;
            byte[]? undecodableRemoteAddress = null;
            if (extension.TryGetTlv(TlvConstants.RemoteAddress, out var baseRemoteAddressTlv))
            {
                var tlvConverter = _tlvConverterFactory.GetConverter<RemoteAddressTlv>()
                                ?? throw new SerializationException(
                                       $"No serializer found for tlv type {nameof(RemoteAddressTlv)}");
                try
                {
                    remoteAddressTlv = tlvConverter.ConvertFromBase(baseRemoteAddressTlv!);
                }
                catch (Exception e) when (e is InvalidCastException or ArgumentException)
                {
                    // remote_addr is odd and advisory (BOLT 1): an address we cannot decode must not fail the init
                    // (NL-344). The receiver logs and drops it.
                    undecodableRemoteAddress = baseRemoteAddressTlv!.Value;
                }
            }

            return new InitMessage(payload, networksTlv, remoteAddressTlv)
            {
                UndecodableRemoteAddress = undecodableRemoteAddress
            };
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException("Error deserializing InitMessage", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}