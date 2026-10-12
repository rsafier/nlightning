namespace NLightning.Domain.LiquidityAds.Enums;

/// <summary>
/// Why a liquidity request or a seller's answer is refused (<see cref="LiquidityAdsRules"/>; Eclair's
/// <c>InvalidLiquidityAds*</c> and <c>MissingLiquidityAds</c>).
/// </summary>
public enum LiquidityAdsRefusal
{
    /// <summary>Accepted.</summary>
    None = 0,

    /// <summary>The seller does not support the requested payment type.</summary>
    UnsupportedPaymentType = 1,

    /// <summary>The requested rate is not one the seller offers.</summary>
    UnknownRate = 2,

    /// <summary>The requested amount is outside the rate's range.</summary>
    AmountOutOfRange = 3,

    /// <summary>The seller answered a request without <c>provide_funding</c>.</summary>
    Missing = 4,

    /// <summary>The seller's signature does not verify against its node id.</summary>
    BadSignature = 5,

    /// <summary>The seller contributes less than the requested amount.</summary>
    AmountTooLow = 6,

    /// <summary>The seller answered with another rate than the one requested.</summary>
    RateMismatch = 7,

    /// <summary>The fee is above the buyer's limit.</summary>
    FeeTooHigh = 8
}