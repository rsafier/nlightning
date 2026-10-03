using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

public class FulfillmentPayloadTlvConverter : ITlvConverter<FulfillmentPayloadTlv>
{
    public BaseTlv ConvertToBase(FulfillmentPayloadTlv tlv)
    {
        return tlv;
    }

    public FulfillmentPayloadTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.FulfillmentPayload)
        {
            throw new InvalidCastException("Invalid TLV type");
        }

        return new FulfillmentPayloadTlv(baseTlv.Value);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as FulfillmentPayloadTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(FulfillmentPayloadTlv)}"));
    }
}