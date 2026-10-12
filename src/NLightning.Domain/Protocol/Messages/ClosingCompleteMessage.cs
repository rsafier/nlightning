namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a <c>closing_complete</c> message (type 40, BOLT 2 <c>option_simple_close</c>): the closer's own closing
/// transaction, for which it pays the fee, with its signatures of one or more output variants.
/// </summary>
public sealed class ClosingCompleteMessage : BaseChannelMessage
{
    /// <summary>The payload of the message.</summary>
    public new ClosingCompletePayload Payload => (ClosingCompletePayload)base.Payload;

    /// <summary>The <c>closing_tlvs</c>: the closer's signatures.</summary>
    public ClosingSignatures Signatures { get; }

    /// <summary>
    /// The simple taproot <c>closing_tlvs</c> (types 5, 6 and 7): the closer's MuSig2 partial signatures, each with its
    /// closer nonce. Empty on other channels.
    /// </summary>
    public ClosingPartialSignaturesWithNonce PartialSignatures { get; }

    public ClosingCompleteMessage(ClosingCompletePayload payload, ClosingSignatures signatures,
                                  ClosingPartialSignaturesWithNonce? partialSignatures = null)
        : base(MessageTypes.ClosingComplete, payload)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        Signatures = signatures;
        PartialSignatures = partialSignatures ?? new ClosingPartialSignaturesWithNonce();
        Extension = SimpleClosingTlvs.ToStream(signatures, PartialSignatures);
    }
}