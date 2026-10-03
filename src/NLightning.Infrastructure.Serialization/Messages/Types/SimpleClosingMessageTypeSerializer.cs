using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;
using Interfaces;

/// <summary>
/// <c>closing_complete</c> and <c>closing_sig</c> (BOLT 2 <c>option_simple_close</c>): the shared payload, then the
/// <c>closing_tlvs</c> (types 1, 2 and 3, each a 64-byte signature, and the simple taproot types 5, 6 and 7 the subclass
/// reads with its own known set), read strictly: an unknown even type fails the message (BOLT 1), a signature of another
/// length too.
/// </summary>
public abstract class SimpleClosingMessageTypeSerializer<TMessage, TPayload> : IMessageTypeSerializer<TMessage>
    where TMessage : class, IMessage
    where TPayload : SimpleClosingPayload
{
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;

    /// <summary>The TLV types of the message's <c>closing_tlvs</c>: the ECDSA 1-3 and its own taproot types.</summary>
    protected abstract IReadOnlySet<BigSize> KnownTlvTypes { get; }

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
                return Create(payload, new ClosingSignatures(), null);

            var extension = await _tlvStreamSerializer.DeserializeStrictAsync(stream, KnownTlvTypes);
            var signatures = new ClosingSignatures(
                ReadSignature(extension, ClosingSigKind.CloserOutputOnly),
                ReadSignature(extension, ClosingSigKind.CloseeOutputOnly),
                ReadSignature(extension, ClosingSigKind.CloserAndCloseeOutputs));
            return Create(payload, signatures, extension);
        }
        catch (Exception e) when (e is SerializationException or InvalidCastException or PayloadSerializationException
                                      or ArgumentException)
        {
            throw new MessageSerializationException($"Error deserializing {typeof(TMessage).Name}", e);
        }
    }

    async Task<IMessage> IMessageTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }

    /// <summary>
    /// Builds the message from its payload, its ECDSA signatures and the extension, from which it reads its own simple
    /// taproot TLVs (null when the message has no extension).
    /// </summary>
    protected abstract TMessage Create(TPayload payload, ClosingSignatures signatures, TlvStream? extension);

    /// <summary>
    /// The raw value of the taproot <c>closing_tlvs</c> signature of <paramref name="kind"/> (types 5-7), checked to be
    /// <paramref name="length"/> bytes, or null when absent.
    /// </summary>
    protected static byte[]? ReadTaprootSignature(TlvStream? extension, ClosingSigKind kind, int length)
    {
        if (extension is null || !extension.TryGetTlv(ClosingPartialSignatures.TypeOf(kind), out var tlv) || tlv is null)
            return null;

        if (tlv.Value.Length != length)
            throw new SerializationException(
                $"closing_tlvs type {(ulong)tlv.Type} holds {tlv.Value.Length} bytes, not {length}");

        return tlv.Value.ToArray();
    }

    private static CompactSignature? ReadSignature(TlvStream? extension, ClosingSigKind kind)
    {
        if (extension is null || !extension.TryGetTlv(ClosingSignatures.TypeOf(kind), out var tlv) || tlv is null)
            return null;

        if (tlv.Value.Length != CryptoConstants.MaxSignatureSize)
            throw new SerializationException(
                $"closing_tlvs type {(ulong)tlv.Type} holds {tlv.Value.Length} bytes, not a 64-byte signature");

        return new CompactSignature(tlv.Value.ToArray());
    }
}