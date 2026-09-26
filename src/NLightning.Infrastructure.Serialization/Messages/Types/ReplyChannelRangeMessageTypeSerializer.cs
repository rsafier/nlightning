using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;
using Interfaces;

/// <summary>
/// Serializer for <see cref="ReplyChannelRangeMessage"/> (BOLT 7, type 264).
/// </summary>
public class ReplyChannelRangeMessageTypeSerializer : IMessageTypeSerializer<ReplyChannelRangeMessage>
{
    /// <summary>
    /// The <c>reply_channel_range_tlvs</c> types this node understands. BOLT 1: an unknown even type MUST fail the
    /// stream.
    /// </summary>
    private static readonly IReadOnlySet<BigSize> s_knownExtensionTypes =
        new HashSet<BigSize> { TlvConstants.ReplyChannelRangeTimestamps, TlvConstants.ReplyChannelRangeChecksums };

    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    public ReplyChannelRangeMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                  ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not ReplyChannelRangeMessage replyMessage)
            throw new SerializationException($"Message is not of type {nameof(ReplyChannelRangeMessage)}");

        var payloadTypeSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                                 ?? throw new SerializationException("No serializer found for payload type");
        await payloadTypeSerializer.SerializeAsync(message.Payload, stream);

        await _tlvStreamSerializer.SerializeAsync(replyMessage.Extension, stream);
    }

    public async Task<ReplyChannelRangeMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<ReplyChannelRangePayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error serializing payload");

            if (stream.Position >= stream.Length)
                return new ReplyChannelRangeMessage(payload);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, s_knownExtensionTypes);
            extension.TryGetTlv(TlvConstants.ReplyChannelRangeTimestamps, out var timestampsTlv);
            extension.TryGetTlv(TlvConstants.ReplyChannelRangeChecksums, out var checksumsTlv);

            return new ReplyChannelRangeMessage(payload, timestampsTlv, checksumsTlv);
        }
        catch (SerializationException e)
        {
            throw new MessageSerializationException($"Error deserializing {nameof(ReplyChannelRangeMessage)}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}