namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using LiquidityAds;
using LiquidityAds.Models;

/// <summary>
/// Liquidity ads <c>provide_funding</c> (BOLT PR #1153, TLV 1339 as Eclair 0.14.3 sends it; NL-850) in <c>accept_channel2</c>, <c>tx_ack_rbf</c> and <c>splice_ack</c>: the seller's signed answer.
/// <see cref="BaseTlv.Value"/> holds the wire bytes (<see cref="LiquidityAdsCodec"/>).
/// </summary>
public class ProvideFundingTlv : BaseTlv
{
    public ProvideFundingTlv(WillFund willFund) : base(TlvConstants.LiquidityAds)
    {
        ArgumentNullException.ThrowIfNull(willFund);
        WillFund = willFund;
        Value = LiquidityAdsCodec.EncodeWillFund(willFund);
        Length = Value.Length;
    }

    /// <summary>The decoded value.</summary>
    public WillFund WillFund { get; }

    public override int GetHashCode() => HashCode.Combine(Type, WillFund);

    public override bool Equals(object? obj) => obj is ProvideFundingTlv other && WillFund.Equals(other.WillFund);
}