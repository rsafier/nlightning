using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

public class AttributionDataTlvConverter : ITlvConverter<AttributionDataTlv>
{
    public BaseTlv ConvertToBase(AttributionDataTlv tlv)
    {
        return tlv;
    }

    public AttributionDataTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.AttributionData)
        {
            throw new InvalidCastException("Invalid TLV type");
        }

        if (baseTlv.Length != AttributionDataTlv.ValueLength)
        {
            throw new InvalidCastException("Invalid length");
        }

        return new AttributionDataTlv(baseTlv.Value);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as AttributionDataTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(AttributionDataTlv)}"));
    }
}