namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using LiquidityAds;
using LiquidityAds.Models;

/// <summary>
/// Liquidity ads <c>request_funding</c> (BOLT PR #1153, TLV 1339 as Eclair 0.14.3 sends it; NL-771) in <c>open_channel2</c>, <c>tx_init_rbf</c> and <c>splice_init</c>: the buyer's request for inbound liquidity.
/// <see cref="BaseTlv.Value"/> holds the wire bytes (<see cref="LiquidityAdsCodec"/>).
/// </summary>
public class RequestFundingTlv : BaseTlv
{
    public RequestFundingTlv(RequestFunding request) : base(TlvConstants.LiquidityAds)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        Value = LiquidityAdsCodec.EncodeRequestFunding(request);
        Length = Value.Length;
    }

    /// <summary>The decoded value.</summary>
    public RequestFunding Request { get; }

    public override int GetHashCode() => HashCode.Combine(Type, Request);

    public override bool Equals(object? obj) => obj is RequestFundingTlv other && Request.Equals(other.Request);
}