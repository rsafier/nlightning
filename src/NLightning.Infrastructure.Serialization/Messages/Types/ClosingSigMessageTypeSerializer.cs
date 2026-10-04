using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Interfaces;

/// <summary>
/// Serializes <c>closing_sig</c> (type 41): the ECDSA signatures 1-3, the simple taproot partial signatures 5-7
/// (32 bytes each) and <c>next_closee_nonce</c> (22).
/// </summary>
public sealed class ClosingSigMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                    ITlvConverterFactory tlvConverterFactory,
                                                    ITlvStreamSerializer tlvStreamSerializer)
    : SimpleClosingMessageTypeSerializer<ClosingSigMessage, ClosingSigPayload>(payloadSerializerFactory,
                                                                                tlvStreamSerializer)
{
    private static readonly IReadOnlySet<BigSize> s_knownTlvTypes =
        ClosingSignatures.TlvTypes.Concat(ClosingPartialSignatures.TlvTypes)
                         .Append(TaprootTlvConstants.NextCloseeNonce).ToHashSet();

    protected override IReadOnlySet<BigSize> KnownTlvTypes => s_knownTlvTypes;

    protected override ClosingSigMessage Create(ClosingSigPayload payload, ClosingSignatures signatures,
                                                TlvStream? extension) =>
        new(payload, signatures,
            new ClosingPartialSignatures(Read(extension, ClosingSigKind.CloserOutputOnly),
                                         Read(extension, ClosingSigKind.CloseeOutputOnly),
                                         Read(extension, ClosingSigKind.CloserAndCloseeOutputs)),
            extension.ReadTlv<NextCloseeNonceTlv>(TaprootTlvConstants.NextCloseeNonce, tlvConverterFactory));

    private static MusigPartialSignature? Read(TlvStream? extension, ClosingSigKind kind)
    {
        var value = ReadTaprootSignature(extension, kind, MusigConstants.PartialSignatureLen);
        return value is null ? null : new MusigPartialSignature(value);
    }
}