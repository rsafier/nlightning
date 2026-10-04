namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a funding_signed message.
/// </summary>
/// <remarks>
/// The funding_signed message is sent by the funder to the fundee after the funding transaction has been created.
/// The message type is 35.
/// </remarks>
public sealed class FundingSignedMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new FundingSignedPayload Payload { get => (FundingSignedPayload)base.Payload; }

    /// <summary>
    /// Simple taproot channels <c>partial_signature_with_nonce</c> (TLV 2): the MuSig2 partial signature of the
    /// peer's commitment and its signing nonce; the payload's <c>signature</c> is then all zeros
    /// (<see cref="Crypto.ValueObjects.CompactSignature.Zero"/>). Absent on other channels.
    /// </summary>
    public PartialSignatureWithNonceTlv? PartialSignatureWithNonceTlv { get; }

    public FundingSignedMessage(FundingSignedPayload payload, PartialSignatureWithNonceTlv? partialSignatureWithNonceTlv = null)
        : base(MessageTypes.FundingSigned, payload)
    {
        PartialSignatureWithNonceTlv = partialSignatureWithNonceTlv;

        if (PartialSignatureWithNonceTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(PartialSignatureWithNonceTlv);
        }
    }
}