namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Money;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 18 (<c>total_amount_msat</c>), a tu64 used by blinded final hops.
/// </summary>
public class TotalAmountMsatTlv : BaseTlv
{
    /// <summary>
    /// The total amount of the payment.
    /// </summary>
    public LightningMoney TotalAmount { get; }

    public TotalAmountMsatTlv(LightningMoney totalAmount) : base(OnionPayloadTlvTypes.TotalAmountMsat)
    {
        TotalAmount = totalAmount;

        Value = TruncatedIntEncoder.Encode(totalAmount.MilliSatoshi);
        Length = Value.Length;
    }
}