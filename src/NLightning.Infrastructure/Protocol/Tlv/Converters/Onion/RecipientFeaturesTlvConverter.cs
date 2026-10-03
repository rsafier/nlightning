using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class RecipientFeaturesTlvConverter : ITlvConverter<RecipientFeaturesTlv>
{
    public BaseTlv ConvertToBase(RecipientFeaturesTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.Features.ToArray());
    }

    public RecipientFeaturesTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.RecipientFeatures)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length)
            throw new InvalidCastException("Invalid length");

        return new RecipientFeaturesTlv(baseTlv.Value);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as RecipientFeaturesTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(RecipientFeaturesTlv)}"));
    }
}