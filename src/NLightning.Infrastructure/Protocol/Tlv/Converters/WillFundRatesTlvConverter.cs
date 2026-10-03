using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.LiquidityAds;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts the liquidity ads <c>option_will_fund</c> record (TLV 1339, NL-850) with <see cref="LiquidityAdsCodec"/>; a value that
/// does not decode strictly is refused.
/// </summary>
public class WillFundRatesTlvConverter : ITlvConverter<WillFundRatesTlv>
{
    public BaseTlv ConvertToBase(WillFundRatesTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.Value);
    }

    public WillFundRatesTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.LiquidityAds)
            throw new InvalidCastException("Invalid TLV type");

        if (!LiquidityAdsCodec.TryDecodeWillFundRates(baseTlv.Value, out var rates) || rates is null)
            throw new InvalidCastException("Invalid option_will_fund value");

        return new WillFundRatesTlv(rates);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as WillFundRatesTlv
                          ?? throw new InvalidCastException($"Error casting BaseTlv to {nameof(WillFundRatesTlv)}"));
    }
}