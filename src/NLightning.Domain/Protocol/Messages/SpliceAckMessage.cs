namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a splice_ack message (BOLT 2 "Channel Splicing", type 81, SP-W-02).
/// </summary>
/// <remarks>
/// The acceptor's answer to <c>splice_init</c> (a rejection is <c>tx_abort</c> instead). TLV 2
/// <c>require_confirmed_inputs</c> is the only known TLV; the serializers are lane SP1-A's (SP1-A-T1).
/// </remarks>
public sealed class SpliceAckMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new SpliceAckPayload Payload { get => (SpliceAckPayload)base.Payload; }

    /// <summary>
    /// <c>splice_ack_tlvs</c> type 2: the sender requires the receiver to add only confirmed inputs (SP-TX-04).
    /// </summary>
    public RequireConfirmedInputsTlv? RequireConfirmedInputsTlv { get; }

    public SpliceAckMessage(SpliceAckPayload payload, RequireConfirmedInputsTlv? requireConfirmedInputsTlv = null)
        : base(MessageTypes.SpliceAck, payload)
    {
        RequireConfirmedInputsTlv = requireConfirmedInputsTlv;

        if (RequireConfirmedInputsTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(RequireConfirmedInputsTlv);
        }
    }
}