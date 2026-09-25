namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Money;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 2 (<c>amt_to_forward</c>), a tu64 amount in millisatoshis.
/// </summary>
public class AmtToForwardTlv : BaseTlv
{
    /// <summary>
    /// The amount to forward to the next hop (or to receive, at the final hop).
    /// </summary>
    public LightningMoney AmountToForward { get; }

    public AmtToForwardTlv(LightningMoney amountToForward) : base(OnionPayloadTlvTypes.AmtToForward)
    {
        AmountToForward = amountToForward;

        Value = TruncatedIntEncoder.Encode(amountToForward.MilliSatoshi);
        Length = Value.Length;
    }
}