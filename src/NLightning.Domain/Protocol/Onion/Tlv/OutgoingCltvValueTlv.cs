namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 4 (<c>outgoing_cltv_value</c>), a tu32 block height.
/// </summary>
public class OutgoingCltvValueTlv : BaseTlv
{
    /// <summary>
    /// The CLTV expiry the outgoing HTLC should have.
    /// </summary>
    public uint OutgoingCltvValue { get; }

    public OutgoingCltvValueTlv(uint outgoingCltvValue) : base(OnionPayloadTlvTypes.OutgoingCltvValue)
    {
        OutgoingCltvValue = outgoingCltvValue;

        Value = TruncatedInt.EncodeTu32(outgoingCltvValue);
        Length = Value.Length;
    }
}