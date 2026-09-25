using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Infrastructure.Converters;

public class OutgoingCltvValueTlvConverter : ITlvConverter<OutgoingCltvValueTlv>
{
    public BaseTlv ConvertToBase(OutgoingCltvValueTlv tlv)
    {
        return new BaseTlv(tlv.Type, TruncatedInt.EncodeTu32(tlv.OutgoingCltvValue));
    }

    public OutgoingCltvValueTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.OutgoingCltvValue)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length
         || !TruncatedInt.TryDecodeTu32(baseTlv.Value, out var cltv))
            throw new InvalidCastException("Invalid length");

        return new OutgoingCltvValueTlv(cltv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as OutgoingCltvValueTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(OutgoingCltvValueTlv)}"));
    }
}