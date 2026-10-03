using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.LiquidityAds;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts the liquidity ads <c>provide_funding</c> record (TLV 1339, NL-850) with <see cref="LiquidityAdsCodec"/>; a value that
/// does not decode strictly is refused.
/// </summary>
public class ProvideFundingTlvConverter : ITlvConverter<ProvideFundingTlv>
{
    public BaseTlv ConvertToBase(ProvideFundingTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.Value);
    }

    public ProvideFundingTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.LiquidityAds)
            throw new InvalidCastException("Invalid TLV type");

        if (!LiquidityAdsCodec.TryDecodeWillFund(baseTlv.Value, out var willFund) || willFund is null)
            throw new InvalidCastException("Invalid provide_funding value");

        return new ProvideFundingTlv(willFund);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as ProvideFundingTlv
                          ?? throw new InvalidCastException($"Error casting BaseTlv to {nameof(ProvideFundingTlv)}"));
    }
}