namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Onion.Constants;

/// <summary>
/// Fulfillment Payload TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>update_fulfill_htlc_tlvs</c> type 3 (<c>fulfillment_payload</c>): an opaque encrypted blob the final node
/// originates and every intermediate node obfuscates with its <c>ammag</c> key (BOLT 4 "Successful Payments").
/// The length is not checked here: BOLT 2 requires a receiver to fail the channel (not the connection) when it is
/// longer than <see cref="OnionConstants.MaxFulfillmentPayloadLength"/>, so the handler checks
/// <see cref="IsTooLong"/>.
/// </remarks>
public class FulfillmentPayloadTlv : BaseTlv
{
    /// <summary>
    /// The (obfuscated) fulfillment payload.
    /// </summary>
    public byte[] FulfillmentPayload => Value;

    /// <summary>
    /// True when the payload is longer than 32768 bytes (BOLT 2: MUST send an <c>error</c> and fail the channel).
    /// </summary>
    public bool IsTooLong => Value.Length > OnionConstants.MaxFulfillmentPayloadLength;

    public FulfillmentPayloadTlv(byte[] fulfillmentPayload) : base(TlvConstants.FulfillmentPayload)
    {
        ArgumentNullException.ThrowIfNull(fulfillmentPayload);

        Value = fulfillmentPayload;
        Length = Value.Length;
    }
}