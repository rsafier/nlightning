using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Messages.Types;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;

/// <summary>Serializes <c>closing_complete</c> (type 40).</summary>
public sealed class ClosingCompleteMessageTypeSerializer(IPayloadSerializerFactory payloadSerializerFactory,
                                                         ITlvStreamSerializer tlvStreamSerializer)
    : SimpleClosingMessageTypeSerializer<ClosingCompleteMessage, ClosingCompletePayload>(payloadSerializerFactory,
        tlvStreamSerializer)
{
    protected override ClosingCompleteMessage Create(ClosingCompletePayload payload, ClosingSignatures signatures) =>
        new(payload, signatures);
}