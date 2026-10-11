namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a commitment_signed message.
/// </summary>
/// <remarks>
/// The commitment_signed message is sent when a node has changes to the remote commitment
/// The message type is 132.
/// </remarks>
public sealed class CommitmentSignedMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new CommitmentSignedPayload Payload { get => (CommitmentSignedPayload)base.Payload; }

    /// <summary>
    /// BOLT 2 <c>commitment_signed_tlvs</c> type 1: the funding transaction spent by this commitment. A sender MUST set
    /// it; a received message may omit it (older peers).
    /// </summary>
    public FundingTxIdTlv? FundingTxIdTlv { get; }

    /// <summary>
    /// Simple taproot channels <c>partial_signature_with_nonce</c> (TLV 2): the MuSig2 partial signature of the
    /// peer's commitment and its signing nonce; the payload's <c>signature</c> is then all zeros
    /// (<see cref="Crypto.ValueObjects.CompactSignature.Zero"/>). Absent on other channels.
    /// </summary>
    public PartialSignatureWithNonceTlv? PartialSignatureWithNonceTlv { get; }

    public CommitmentSignedMessage(CommitmentSignedPayload payload, FundingTxIdTlv? fundingTxIdTlv = null,
                                   PartialSignatureWithNonceTlv? partialSignatureWithNonceTlv = null)
        : base(MessageTypes.CommitmentSigned, payload)
    {
        FundingTxIdTlv = fundingTxIdTlv;
        PartialSignatureWithNonceTlv = partialSignatureWithNonceTlv;

        if (FundingTxIdTlv is not null || PartialSignatureWithNonceTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(FundingTxIdTlv, PartialSignatureWithNonceTlv);
        }
    }
}