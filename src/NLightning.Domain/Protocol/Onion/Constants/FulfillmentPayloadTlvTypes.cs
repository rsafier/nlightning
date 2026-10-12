using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Constants;

/// <summary>
/// TLV types of the BOLT 4 <c>fulfillment_payload_tlvs</c> stream (the plaintext of an <c>update_fulfill_htlc</c>
/// <c>fulfillment_payload</c>).
/// </summary>
/// <remarks>
/// These numbers live in their own namespace and must not be mixed with <c>TlvConstants</c>.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class FulfillmentPayloadTlvTypes
{
    /// <summary>
    /// padding: conceals the length of the conveyed data; ignored by the origin.
    /// </summary>
    public const ulong Padding = 1;
}