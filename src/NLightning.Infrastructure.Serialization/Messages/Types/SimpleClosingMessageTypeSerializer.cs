using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Payloads;
using Exceptions;
using Interfaces;

/// <summary>
/// <c>closing_complete</c> and <c>closing_sig</c> (BOLT 2 <c>option_simple_close</c>): the shared payload, then the
/// <c>closing_tlvs</c> (types 1, 2 and 3, each a 64-byte signature), read strictly: an unknown even type fails the
/// message (BOLT 1), a signature of another length too.
/// </summary>
public abstract class SimpleClosingMessageTypeSerializer<TMessage, TPayload> : IMessageTypeSerializer<TMessage>
    where TMessage : class, IMessage
    where TPayload : SimpleClosingPayload
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    protected SimpleClosingMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                 ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
    }

    public async Task SerializeAsync(IMessage message, Stream stream)
    {
        if (message is not TMessage)
            throw new SerializationException($"Message is not of type {typeof(TMessage).Name}");

        var payloadSerializer = _payloadSerializerFactory.GetSerializer(message.Type)
                             ?? throw new SerializationException("No serializer found for payload type");
        await payloadSerializer.SerializeAsync(message.Payload, stream);
        await _tlvStreamSerializer.SerializeAsync(message.Extension, stream);
    }

    public async Task<TMessage> DeserializeAsync(Stream stream)
    {
        try
        {
            var payloadSerializer = _payloadSerializerFactory.GetSerializer<TPayload>()
                                 ?? throw new SerializationException("No serializer found for payload type");
            var payload = await payloadSerializer.DeserializeAsync(stream)
                       ?? throw new SerializationException("Error deserializing payload");

            if (stream.Position >= stream.Length)
                return Create(payload, new ClosingSignatures());

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, ClosingSignatures.TlvTypes);
            var signatures = new ClosingSignatures(
                ReadSignature(extension, ClosingSigKind.CloserOutputOnly),
                ReadSignature(extension, ClosingSigKind.CloseeOutputOnly),
                ReadSignature(extension, ClosingSigKind.CloserAndCloseeOutputs));
            return Create(payload, signatures);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException or PayloadSerializationException)
        {
            throw new MessageSerializationException($"Error deserializing {typeof(TMessage).Name}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }

    /// <summary>Builds the message from its payload and signatures.</summary>
    protected abstract TMessage Create(TPayload payload, ClosingSignatures signatures);

    private static CompactSignature? ReadSignature(Domain.Protocol.Models.TlvStream? extension, ClosingSigKind kind)
    {
        if (extension is null || !extension.TryGetTlv(ClosingSignatures.TypeOf(kind), out var tlv) || tlv is null)
            return null;

        if (tlv.Value.Length != CryptoConstants.MaxSignatureSize)
            throw new SerializationException(
                $"closing_tlvs type {(ulong)tlv.Type} holds {tlv.Value.Length} bytes, not a 64-byte signature");

        return new CompactSignature(tlv.Value.ToArray());
    }
}