namespace NLightning.Domain.LiquidityAds.Enums;

/// <summary>
/// How the buyer pays the liquidity fee: the bit index in <c>will_fund_rates.payment_types</c> and the type of the
/// <c>payment_details</c> record (BOLT PR #1153; the 128-130 types are Eclair's on-the-fly funding).
/// </summary>
public enum LiquidityPaymentType
{
    /// <summary>
    /// The fee moves from the buyer's channel balance to the seller's in the interactive transaction's commitment.
    /// </summary>
    FromChannelBalance = 0,

    /// <summary>The fee is deducted from future HTLCs relayed to the buyer (Eclair on-the-fly funding).</summary>
    FromFutureHtlc = 128,

    /// <summary>As <see cref="FromFutureHtlc"/>, with the preimages revealed at once (Eclair on-the-fly funding).</summary>
    FromFutureHtlcWithPreimage = 129,

    /// <summary>
    /// As <see cref="FromChannelBalance"/>, with HTLCs expected after the funding (Eclair on-the-fly funding).
    /// </summary>
    FromChannelBalanceForFutureHtlc = 130
}