namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a funding_created message.
/// </summary>
/// <remarks>
/// The funding_created message is sent by the funder to the fundee after the funding transaction has been created.
/// The message type is 34.
/// </remarks>
public sealed class FundingCreatedMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new FundingCreatedPayload Payload { get => (FundingCreatedPayload)base.Payload; }

    /// <summary>
    /// Simple taproot channels <c>partial_signature_with_nonce</c> (TLV 2): the MuSig2 partial signature of the
    /// peer's commitment and its signing nonce; the payload's <c>signature</c> is then all zeros
    /// (<see cref="Crypto.ValueObjects.CompactSignature.Zero"/>). Absent on other channels.
    /// </summary>
    public PartialSignatureWithNonceTlv? PartialSignatureWithNonceTlv { get; }

    public FundingCreatedMessage(FundingCreatedPayload payload, PartialSignatureWithNonceTlv? partialSignatureWithNonceTlv = null)
        : base(MessageTypes.FundingCreated, payload)
    {
        PartialSignatureWithNonceTlv = partialSignatureWithNonceTlv;

        if (PartialSignatureWithNonceTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(PartialSignatureWithNonceTlv);
        }
    }
}