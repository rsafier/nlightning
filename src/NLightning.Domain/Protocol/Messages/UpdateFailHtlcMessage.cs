namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a update_fail_htlc message.
/// </summary>
/// <remarks>
/// The update_fail_htlc message is sent to let the peer know that the htlc has failed
/// The message type is 131.
/// </remarks>
public sealed class UpdateFailHtlcMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new UpdateFailHtlcPayload Payload { get => (UpdateFailHtlcPayload)base.Payload; }

    /// <summary>
    /// The optional <c>attribution_data</c> (TLV 1, BOLT 4 attributable failures).
    /// </summary>
    public AttributionDataTlv? AttributionDataTlv { get; }

    public UpdateFailHtlcMessage(UpdateFailHtlcPayload payload, AttributionDataTlv? attributionDataTlv = null)
        : base(MessageTypes.UpdateFailHtlc, payload)
    {
        AttributionDataTlv = attributionDataTlv;

        if (AttributionDataTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(AttributionDataTlv);
        }
    }
}