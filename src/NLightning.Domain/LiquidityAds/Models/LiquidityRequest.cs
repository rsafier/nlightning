namespace NLightning.Domain.LiquidityAds.Models;

/// <summary>
/// What a buyer asks a seller for in a dual-funded open, a splice or one of their RBF attempts (NL-771): the amount the
/// seller should add to the funding output, optionally the seller rate to buy at (else its cheapest rate that sells the
/// amount, from the seller's <c>init</c> or, failing that, its <c>node_announcement</c>), and our limit on the fee.
/// </summary>
/// <param name="AmountSat">The inbound liquidity we buy, in satoshis.</param>
/// <param name="Rate">The seller rate to buy at; null picks the cheapest compatible one.</param>
/// <param name="MaxFeeSat">The most we pay (mining + service fee); null = <c>Node:LiquidityAds:MaxFeeSat</c>.</param>
public sealed record LiquidityRequest(ulong AmountSat, FundingRate? Rate = null, ulong? MaxFeeSat = null);