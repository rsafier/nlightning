using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.LiquidityAds;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts the liquidity ads <c>request_funding</c> record (TLV 1339, NL-850) with <see cref="LiquidityAdsCodec"/>; a value that
/// does not decode strictly is refused.
/// </summary>
public class RequestFundingTlvConverter : ITlvConverter<RequestFundingTlv>
{
    public BaseTlv ConvertToBase(RequestFundingTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.Value);
    }

    public RequestFundingTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.LiquidityAds)
            throw new InvalidCastException("Invalid TLV type");

        if (!LiquidityAdsCodec.TryDecodeRequestFunding(baseTlv.Value, out var request) || request is null)
            throw new InvalidCastException("Invalid request_funding value");

        return new RequestFundingTlv(request);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as RequestFundingTlv
                          ?? throw new InvalidCastException($"Error casting BaseTlv to {nameof(RequestFundingTlv)}"));
    }
}