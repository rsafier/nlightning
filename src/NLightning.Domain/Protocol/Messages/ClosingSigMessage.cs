namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a <c>closing_sig</c> message (type 41, BOLT 2 <c>option_simple_close</c>): the closee's signature of the
/// closing transaction a <c>closing_complete</c> proposed, in the same TLV field.
/// </summary>
public sealed class ClosingSigMessage : BaseChannelMessage
{
    /// <summary>The payload of the message.</summary>
    public new ClosingSigPayload Payload => (ClosingSigPayload)base.Payload;

    /// <summary>The <c>closing_tlvs</c>: exactly one signature when the sender follows BOLT 2.</summary>
    public ClosingSignatures Signatures { get; }

    public ClosingSigMessage(ClosingSigPayload payload, ClosingSignatures signatures)
        : base(MessageTypes.ClosingSig, payload)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        Signatures = signatures;
        Extension = SimpleClosingTlvs.ToStream(signatures);
    }
}