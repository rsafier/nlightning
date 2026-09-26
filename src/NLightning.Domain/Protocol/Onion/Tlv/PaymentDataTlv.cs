namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;
using Money;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 8 (<c>payment_data</c>): 32-byte payment_secret || tu64 total_msat.
/// </summary>
public class PaymentDataTlv : BaseTlv
{
    /// <summary>
    /// The payment secret from the invoice.
    /// </summary>
    public Secret PaymentSecret { get; }

    /// <summary>
    /// The total amount of the (possibly multi-part) payment.
    /// </summary>
    public LightningMoney TotalMsat { get; }

    public PaymentDataTlv(Secret paymentSecret, LightningMoney totalMsat) : base(OnionPayloadTlvTypes.PaymentData)
    {
        byte[] secretBytes = paymentSecret;
        if (secretBytes.Length != CryptoConstants.SecretLen)
            throw new ArgumentException($"Payment secret must be {CryptoConstants.SecretLen} bytes.",
                                        nameof(paymentSecret));

        PaymentSecret = paymentSecret;
        TotalMsat = totalMsat;

        var total = TruncatedInt.EncodeTu64(totalMsat.MilliSatoshi);
        var value = new byte[CryptoConstants.SecretLen + total.Length];
        secretBytes.CopyTo(value, 0);
        total.CopyTo(value, CryptoConstants.SecretLen);

        Value = value;
        Length = value.Length;
    }
}