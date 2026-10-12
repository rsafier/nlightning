namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;
using Tlv;

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

    /// <summary>
    /// The simple taproot <c>closing_tlvs</c> (types 5, 6 and 7): the closee's MuSig2 partial signature (exactly one when
    /// the sender follows the spec). Empty on other channels.
    /// </summary>
    public ClosingPartialSignatures PartialSignatures { get; }

    /// <summary>
    /// Simple taproot channels <c>next_closee_nonce</c> (TLV 22): the closee's nonce for the next
    /// <c>closing_complete</c>. LND 0.21 requires it on a taproot channel.
    /// </summary>
    public NextCloseeNonceTlv? NextCloseeNonceTlv { get; }

    public ClosingSigMessage(ClosingSigPayload payload, ClosingSignatures signatures,
                             ClosingPartialSignatures? partialSignatures = null,
                             NextCloseeNonceTlv? nextCloseeNonceTlv = null)
        : base(MessageTypes.ClosingSig, payload)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        Signatures = signatures;
        PartialSignatures = partialSignatures ?? new ClosingPartialSignatures();
        NextCloseeNonceTlv = nextCloseeNonceTlv;
        Extension = SimpleClosingTlvs.ToStream(signatures, partialSignatures: PartialSignatures,
                                               nextCloseeNonceTlv: NextCloseeNonceTlv);
    }
}