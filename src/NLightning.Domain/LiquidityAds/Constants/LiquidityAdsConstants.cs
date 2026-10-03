namespace NLightning.Domain.LiquidityAds.Constants;

/// <summary>
/// Constants of liquidity ads (BOLT PR #1153 "Extensible Liquidity Ads", as Eclair 0.14.3 speaks it; NL-771).
/// </summary>
public static class LiquidityAdsConstants
{
    /// <summary>
    /// The TLV type of every liquidity ads record: <c>request_funding</c> (open_channel2, tx_init_rbf, splice_init),
    /// <c>provide_funding</c> (accept_channel2, tx_ack_rbf, splice_ack) and <c>option_will_fund</c> (init,
    /// node_announcement). Eclair 0.14.3 uses this temporary odd type while the spec is under review; the one place to
    /// change when #1153 is merged.
    /// </summary>
    public const ulong TlvType = 1339;

    /// <summary>The tag hashed in front of the funding rate and funding script the seller signs.</summary>
    public const string SignatureTag = "liquidity_ads_purchase";

    /// <summary>How long a seller keeps a sold channel open (about a month, BOLT PR #1153 SHOULD).</summary>
    public const uint LeaseBlocks = 4032;

    /// <summary>The encoded length of a <see cref="Models.FundingRate"/>.</summary>
    public const int FundingRateLength = 20;

    /// <summary>The length of the seller's signature in <c>will_fund</c>.</summary>
    public const int SignatureLength = 64;
}