namespace NLightning.Domain.Protocol.Onion.Models;

using Enums;
using Protocol.Tlv;

/// <summary>
/// What the origin node learned from an <c>update_fulfill_htlc</c>'s <c>attribution_data</c> and
/// <c>fulfillment_payload</c> (BOLT 4 "Successful Payments").
/// </summary>
public sealed class VerifiedFulfillment
{
    /// <summary>
    /// The verified hold times of the hops.
    /// </summary>
    public AttributionVerification Attribution { get; }

    /// <summary>
    /// Whether a <c>fulfillment_payload</c> was received and whether it decrypted to a valid TLV stream.
    /// </summary>
    public FulfillmentPayloadStatus PayloadStatus { get; }

    /// <summary>
    /// The records of a valid <c>fulfillment_payload_tlvs</c> stream other than <c>padding</c>, in type order (empty
    /// unless <see cref="PayloadStatus"/> is <see cref="FulfillmentPayloadStatus.Valid"/>). Each value holds the raw
    /// wire bytes.
    /// </summary>
    public IReadOnlyList<BaseTlv> PayloadRecords { get; }

    public VerifiedFulfillment(AttributionVerification attribution, FulfillmentPayloadStatus payloadStatus,
                               IReadOnlyList<BaseTlv> payloadRecords)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(payloadRecords);

        Attribution = attribution;
        PayloadStatus = payloadStatus;
        PayloadRecords = payloadRecords;
    }
}