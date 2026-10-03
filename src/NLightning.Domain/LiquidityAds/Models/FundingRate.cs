namespace NLightning.Domain.LiquidityAds.Models;

/// <summary>
/// A rate at which a seller sells inbound liquidity (BOLT PR #1153 <c>funding_rate</c>).
/// </summary>
/// <param name="MinAmountSat">The smallest amount that can be bought at this rate.</param>
/// <param name="MaxAmountSat">The largest amount that can be bought at this rate.</param>
/// <param name="FundingWeight">The weight of the seller's inputs and outputs whose mining fee the buyer refunds.</param>
/// <param name="FeeBasis">The proportional fee, in basis points of the contributed amount.</param>
/// <param name="FeeBaseSat">The flat fee, paid on every purchase.</param>
/// <param name="ChannelCreationFeeSat">The extra flat fee when the purchase opens a new channel.</param>
public readonly record struct FundingRate(
    uint MinAmountSat,
    uint MaxAmountSat,
    ushort FundingWeight,
    ushort FeeBasis,
    uint FeeBaseSat,
    uint ChannelCreationFeeSat)
{
    /// <summary>Whether <paramref name="requestedSat"/> may be bought at this rate.</summary>
    public bool IsCompatible(ulong requestedSat) => MinAmountSat <= requestedSat && requestedSat <= MaxAmountSat;
}