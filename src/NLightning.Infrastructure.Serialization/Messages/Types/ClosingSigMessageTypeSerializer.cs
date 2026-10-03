using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;

/// <summary>Serializes <c>closing_sig</c> (type 41).</summary>
public sealed class ClosingSigMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                    ITlvStreamSerializer tlvStreamSerializer)
    : SimpleClosingMessageTypeSerializer<ClosingSigMessage, ClosingSigPayload>(payloadSerializerFactory,
                                                                                tlvStreamSerializer)
{
    protected override ClosingSigMessage Create(ClosingSigPayload payload, ClosingSignatures signatures) =>
        new(payload, signatures);
}