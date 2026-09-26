namespace NLightning.Domain.Protocol.Onion.Models;

using Constants;

/// <summary>
/// A failure return packet with its <c>attribution_data</c>: what goes into <c>update_fail_htlc.reason</c> and its
/// TLV 1.
/// </summary>
public sealed class AttributedErrorPacket
{
    /// <summary>
    /// The obfuscated return packet (<c>update_fail_htlc.reason</c>).
    /// </summary>
    public byte[] Reason { get; }

    /// <summary>
    /// The obfuscated attribution data (<c>update_fail_htlc_tlvs</c> type 1, 920 bytes).
    /// </summary>
    public byte[] AttributionData { get; }

    public AttributedErrorPacket(byte[] reason, byte[] attributionData)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(attributionData);
        if (attributionData.Length != OnionConstants.AttributionDataLength)
            throw new ArgumentException($"attribution_data must be {OnionConstants.AttributionDataLength} bytes.",
                                        nameof(attributionData));

        Reason = reason;
        AttributionData = attributionData;
    }
}