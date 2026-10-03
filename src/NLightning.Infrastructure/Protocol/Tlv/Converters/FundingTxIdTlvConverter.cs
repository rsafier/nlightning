using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

public class FundingTxIdTlvConverter : ITlvConverter<FundingTxIdTlv>
{
    public BaseTlv ConvertToBase(FundingTxIdTlv tlv)
    {
        return tlv;
    }

    public FundingTxIdTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.FundingTxId)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != FundingTxIdTlv.ValueLength)
            throw new InvalidCastException("Invalid length");

        return new FundingTxIdTlv(baseTlv.Value[..FundingTxIdTlv.ValueLength]);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as FundingTxIdTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(FundingTxIdTlv)}"));
    }
}