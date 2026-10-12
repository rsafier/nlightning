namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// The outcome of reading an <c>update_fulfill_htlc</c> <c>fulfillment_payload</c> at the origin node.
/// </summary>
public enum FulfillmentPayloadStatus
{
    /// <summary>
    /// No <c>fulfillment_payload</c> was received.
    /// </summary>
    None = 0,

    /// <summary>
    /// The payload decrypted and its <c>fulfillment_payload_tlvs</c> stream is well formed.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// BOLT 4: the payload MUST be ignored (invalid Poly1305 tag, malformed lengths, duplicate or unordered types, or
    /// unknown even types).
    /// </summary>
    Invalid = 2
}