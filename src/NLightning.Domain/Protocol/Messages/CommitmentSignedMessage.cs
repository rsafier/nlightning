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

    public CommitmentSignedMessage(CommitmentSignedPayload payload, FundingTxIdTlv? fundingTxIdTlv = null)
        : base(MessageTypes.CommitmentSigned, payload)
    {
        FundingTxIdTlv = fundingTxIdTlv;

        if (FundingTxIdTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(FundingTxIdTlv);
        }
    }
}