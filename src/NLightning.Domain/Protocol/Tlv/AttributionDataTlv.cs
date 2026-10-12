namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Onion.Constants;

/// <summary>
/// Attribution Data TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>update_fulfill_htlc_tlvs</c> and <c>update_fail_htlc_tlvs</c> type 1 (<c>attribution_data</c>):
/// [<c>20*u32</c>:<c>htlc_hold_times</c>] [<c>210*sha256[..4]</c>:<c>truncated_hmacs</c>], 920 bytes in all. The
/// bytes are opaque here; BOLT 4 "Returning Errors" (<c>IAttributionDataService</c>) builds and verifies them.
/// </remarks>
public class AttributionDataTlv : BaseTlv
{
    /// <summary>
    /// The size of the TLV value (920 bytes).
    /// </summary>
    public const int ValueLength = OnionConstants.AttributionDataLength;

    /// <summary>
    /// The (obfuscated) attribution data.
    /// </summary>
    public byte[] AttributionData => Value;

    /// <exception cref="ArgumentException">If <paramref name="attributionData"/> is not 920 bytes.</exception>
    public AttributionDataTlv(byte[] attributionData) : base(TlvConstants.AttributionData)
    {
        ArgumentNullException.ThrowIfNull(attributionData);
        if (attributionData.Length != ValueLength)
            throw new ArgumentException($"attribution_data must be {ValueLength} bytes, got {attributionData.Length}.",
                                        nameof(attributionData));

        Value = attributionData;
        Length = Value.Length;
    }
}