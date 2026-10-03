using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Interfaces;

/// <summary>
/// Serializes <c>closing_complete</c> (type 40): the ECDSA signatures 1-3 and the simple taproot partial signatures with
/// the closer's nonce 5-7 (98 bytes each).
/// </summary>
public sealed class ClosingCompleteMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                         ITlvStreamSerializer tlvStreamSerializer)
    : SimpleClosingMessageTypeSerializer<ClosingCompleteMessage, ClosingCompletePayload>(payloadSerializerFactory,
        tlvStreamSerializer)
{
    private static readonly IReadOnlySet<BigSize> s_knownTlvTypes =
        ClosingSignatures.TlvTypes.Concat(ClosingPartialSignaturesWithNonce.TlvTypes).ToHashSet();

    protected override IReadOnlySet<BigSize> KnownTlvTypes => s_knownTlvTypes;

    protected override ClosingCompleteMessage Create(ClosingCompletePayload payload, ClosingSignatures signatures,
                                                     TlvStream? extension) =>
        new(payload, signatures, new ClosingPartialSignaturesWithNonce(
                Read(extension, ClosingSigKind.CloserOutputOnly),
                Read(extension, ClosingSigKind.CloseeOutputOnly),
                Read(extension, ClosingSigKind.CloserAndCloseeOutputs)));

    private static MusigPartialSignatureWithNonce? Read(TlvStream? extension, ClosingSigKind kind)
    {
        var value = ReadTaprootSignature(extension, kind, MusigConstants.PartialSignatureWithNonceLen);
        return value is null ? null : new MusigPartialSignatureWithNonce(value);
    }
}