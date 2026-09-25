using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Infrastructure.Converters;

public class AmtToForwardTlvConverter : ITlvConverter<AmtToForwardTlv>
{
    public BaseTlv ConvertToBase(AmtToForwardTlv tlv)
    {
        return new BaseTlv(tlv.Type, TruncatedInt.EncodeTu64(tlv.AmountToForward.MilliSatoshi));
    }

    public AmtToForwardTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.AmtToForward)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length
         || !TruncatedInt.TryDecodeTu64(baseTlv.Value, out var amount))
            throw new InvalidCastException("Invalid length");

        return new AmtToForwardTlv(LightningMoney.MilliSatoshis(amount));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as AmtToForwardTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {nameof(AmtToForwardTlv)}"));
    }
}