namespace NLightning.Domain.LiquidityAds.Models;

/// <summary>
/// The fee of a liquidity purchase (Eclair <c>LiquidityAds.Fees</c>).
/// </summary>
/// <param name="MiningFeeSat">The refund of the seller's on-chain fee for <see cref="FundingRate.FundingWeight"/>.</param>
/// <param name="ServiceFeeSat">The seller's fee: flat, channel creation and proportional parts.</param>
public readonly record struct LiquidityFees(ulong MiningFeeSat, ulong ServiceFeeSat)
{
    /// <summary>The total fee, in satoshis.</summary>
    public ulong TotalSat => checked(MiningFeeSat + ServiceFeeSat);

    /// <summary>The total fee, in millisatoshis.</summary>
    public ulong TotalMsat => checked(TotalSat * 1_000);
}