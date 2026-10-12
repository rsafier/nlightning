namespace NLightning.Domain.Protocol.Onion.Models;

using Constants;

/// <summary>
/// The success-path data sent upstream in an <c>update_fulfill_htlc</c>: its <c>attribution_data</c> (TLV 1) and, when
/// there is one, its <c>fulfillment_payload</c> (TLV 3).
/// </summary>
public sealed class AttributedFulfillment
{
    /// <summary>
    /// The obfuscated attribution data (920 bytes).
    /// </summary>
    public byte[] AttributionData { get; }

    /// <summary>
    /// The (obfuscated) fulfillment payload, or <c>null</c> when there is none.
    /// </summary>
    public byte[]? FulfillmentPayload { get; }

    public AttributedFulfillment(byte[] attributionData, byte[]? fulfillmentPayload)
    {
        ArgumentNullException.ThrowIfNull(attributionData);
        if (attributionData.Length != OnionConstants.AttributionDataLength)
            throw new ArgumentException($"attribution_data must be {OnionConstants.AttributionDataLength} bytes.",
                                        nameof(attributionData));

        AttributionData = attributionData;
        FulfillmentPayload = fulfillmentPayload;
    }
}