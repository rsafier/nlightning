using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class TotalAmountMsatTlvConverter : ITlvConverter<TotalAmountMsatTlv>
{
    public BaseTlv ConvertToBase(TotalAmountMsatTlv tlv)
    {
        return new BaseTlv(tlv.Type, OnionTruncatedInt.Encode(tlv.TotalAmount.MilliSatoshi));
    }

    public TotalAmountMsatTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.TotalAmountMsat)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length
         || !OnionTruncatedInt.TryDecodeTu64(baseTlv.Value, out var amount))
            throw new InvalidCastException("Invalid length");

        return new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(amount));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as TotalAmountMsatTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(TotalAmountMsatTlv)}"));
    }
}