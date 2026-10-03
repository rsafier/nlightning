namespace NLightning.Domain.LiquidityAds.Models;

/// <summary>
/// A buyer's request for inbound liquidity (BOLT PR #1153 <c>request_funds</c>, in <c>open_channel2</c>,
/// <c>tx_init_rbf</c> and <c>splice_init</c>).
/// </summary>
/// <param name="RequestedSat">The amount the seller should contribute.</param>
/// <param name="Rate">The seller's rate the buyer accepts.</param>
/// <param name="PaymentDetails">How the fee is paid.</param>
public sealed record RequestFunding(ulong RequestedSat, FundingRate Rate, LiquidityPaymentDetails PaymentDetails);