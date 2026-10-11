namespace NLightning.Domain.LiquidityAds.Enums;

/// <summary>
/// Where a liquidity purchase is (<c>LiquidityPurchaseModel</c>). Persisted as a byte: never renumber.
/// </summary>
/// <remarks>
/// Pending → Active → Closed, Pending → Replaced, or Pending → Closed (the channel closed before the attempt
/// confirmed).
/// </remarks>
public enum LiquidityPurchaseStatus : byte
{
    /// <summary>Negotiated and signed; the funding attempt it belongs to has not confirmed.</summary>
    Pending = 1,

    /// <summary>The funding attempt confirmed: the lease runs from that height.</summary>
    Active = 2,

    /// <summary>Another attempt of the same channel funding (an RBF sibling) confirmed instead.</summary>
    Replaced = 3,

    /// <summary>The channel closed.</summary>
    Closed = 4
}