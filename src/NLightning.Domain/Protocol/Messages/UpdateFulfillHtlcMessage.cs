namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a update_fulfill_htlc message.
/// </summary>
/// <remarks>
/// The update_fulfill_htlc message is sent to let the peer know that the htlc was fulfiled
/// The message type is 130.
/// </remarks>
public sealed class UpdateFulfillHtlcMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new UpdateFulfillHtlcPayload Payload { get => (UpdateFulfillHtlcPayload)base.Payload; }

    /// <summary>
    /// The optional <c>attribution_data</c> (TLV 1): the hold times reported by each hop (BOLT 4).
    /// </summary>
    public AttributionDataTlv? AttributionDataTlv { get; }

    /// <summary>
    /// The optional <c>fulfillment_payload</c> (TLV 3) for the origin (BOLT 4 "Successful Payments").
    /// </summary>
    public FulfillmentPayloadTlv? FulfillmentPayloadTlv { get; }

    public UpdateFulfillHtlcMessage(UpdateFulfillHtlcPayload payload, AttributionDataTlv? attributionDataTlv = null,
                                    FulfillmentPayloadTlv? fulfillmentPayloadTlv = null)
        : base(MessageTypes.UpdateFulfillHtlc, payload)
    {
        AttributionDataTlv = attributionDataTlv;
        FulfillmentPayloadTlv = fulfillmentPayloadTlv;

        if (AttributionDataTlv is null && FulfillmentPayloadTlv is null)
            return;

        Extension = new TlvStream();
        if (AttributionDataTlv is not null)
            Extension.Add(AttributionDataTlv);
        if (FulfillmentPayloadTlv is not null)
            Extension.Add(FulfillmentPayloadTlv);
    }
}