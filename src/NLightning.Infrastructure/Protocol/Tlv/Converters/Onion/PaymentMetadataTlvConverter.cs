using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class PaymentMetadataTlvConverter : ITlvConverter<PaymentMetadataTlv>
{
    public BaseTlv ConvertToBase(PaymentMetadataTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.PaymentMetadata.ToArray());
    }

    public PaymentMetadataTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.PaymentMetadata)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length)
            throw new InvalidCastException("Invalid length");

        return new PaymentMetadataTlv(baseTlv.Value);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as PaymentMetadataTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(PaymentMetadataTlv)}"));
    }
}