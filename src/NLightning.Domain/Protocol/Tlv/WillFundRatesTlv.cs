namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using LiquidityAds;
using LiquidityAds.Models;

/// <summary>
/// Liquidity ads <c>option_will_fund</c> (BOLT PR #1153, TLV 1339 as Eclair 0.14.3 sends it; NL-850) in <c>init</c> (and, in its extra data, <c>node_announcement</c>): the rates the sender sells inbound liquidity at.
/// <see cref="BaseTlv.Value"/> holds the wire bytes (<see cref="LiquidityAdsCodec"/>).
/// </summary>
public class WillFundRatesTlv : BaseTlv
{
    public WillFundRatesTlv(WillFundRates rates) : base(TlvConstants.LiquidityAds)
    {
        ArgumentNullException.ThrowIfNull(rates);
        Rates = rates;
        Value = LiquidityAdsCodec.EncodeWillFundRates(rates);
        Length = Value.Length;
    }

    /// <summary>The decoded value.</summary>
    public WillFundRates Rates { get; }

    public override int GetHashCode() => HashCode.Combine(Type, Rates);

    public override bool Equals(object? obj) => obj is WillFundRatesTlv other && Rates.Equals(other.Rates);
}