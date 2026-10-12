namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 16 (<c>payment_metadata</c>), opaque data from the invoice.
/// </summary>
public class PaymentMetadataTlv : BaseTlv
{
    /// <summary>
    /// The payment metadata.
    /// </summary>
    public ReadOnlyMemory<byte> PaymentMetadata { get; }

    public PaymentMetadataTlv(ReadOnlySpan<byte> paymentMetadata) : base(OnionPayloadTlvTypes.PaymentMetadata)
    {
        var value = paymentMetadata.ToArray();

        Value = value;
        Length = value.Length;
        PaymentMetadata = value;
    }
}