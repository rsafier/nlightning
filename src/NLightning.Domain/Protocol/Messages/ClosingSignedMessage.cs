namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a closing_signed message.
/// </summary>
/// <remarks>
/// The closing_signed message is after shutdown is complete and there are no pending HTLCs.
/// The message type is 39.
/// </remarks>
public sealed class ClosingSignedMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new ClosingSignedPayload Payload { get => (ClosingSignedPayload)base.Payload; }

    /// <summary>
    /// BOLT 2 <c>closing_signed_tlvs</c> type 1. Optional: a sender SHOULD set it, and a peer that does not is
    /// negotiated with the legacy "strictly between" rule.
    /// </summary>
    public FeeRangeTlv? FeeRangeTlv { get; }

    public ClosingSignedMessage(ClosingSignedPayload payload, FeeRangeTlv? feeRangeTlv = null)
        : base(MessageTypes.ClosingSigned, payload)
    {
        FeeRangeTlv = feeRangeTlv;

        if (FeeRangeTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(FeeRangeTlv);
        }
    }
}