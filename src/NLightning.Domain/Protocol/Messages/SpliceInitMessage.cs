namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a splice_init message (BOLT 2 "Channel Splicing", type 80, SP-W-01).
/// </summary>
/// <remarks>
/// Sent by the quiescence initiator of a quiescent channel to start a splice (SP-S-01). TLV 2
/// <c>require_confirmed_inputs</c> is the only known TLV; the serializers are lane SP1-A's (SP1-A-T1).
/// </remarks>
public sealed class SpliceInitMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new SpliceInitPayload Payload { get => (SpliceInitPayload)base.Payload; }

    /// <summary>
    /// <c>splice_init_tlvs</c> type 2: the sender requires the receiver to add only confirmed inputs (SP-TX-04).
    /// </summary>
    public RequireConfirmedInputsTlv? RequireConfirmedInputsTlv { get; }

    public SpliceInitMessage(SpliceInitPayload payload, RequireConfirmedInputsTlv? requireConfirmedInputsTlv = null)
        : base(MessageTypes.SpliceInit, payload)
    {
        RequireConfirmedInputsTlv = requireConfirmedInputsTlv;

        if (RequireConfirmedInputsTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(RequireConfirmedInputsTlv);
        }
    }
}