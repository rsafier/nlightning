namespace NLightning.Domain.LiquidityAds.Enums;

/// <summary>
/// Our side of a liquidity purchase (<c>LiquidityPurchaseModel</c>). Persisted as a byte: never renumber.
/// </summary>
public enum LiquidityPurchaseRole : byte
{
    /// <summary>We bought: we sent <c>request_funding</c> and the peer answered <c>provide_funding</c>.</summary>
    Buyer = 1,

    /// <summary>We sold: the peer sent <c>request_funding</c> and we answered <c>provide_funding</c>.</summary>
    Seller = 2
}