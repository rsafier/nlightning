namespace NLightning.Domain.Node.Options;

using LiquidityAds.Constants;
using LiquidityAds.Enums;
using LiquidityAds.Models;

/// <summary>
/// Liquidity ads (BOLT PR #1153 as Eclair 0.14.3 speaks it; NL-771): the rates we sell inbound liquidity at and the
/// limits of our sales and purchases. Bound from the <c>Node:LiquidityAds</c> configuration section (it is
/// <see cref="NodeOptions.LiquidityAds"/>).
/// </summary>
/// <remarks>
/// Selling is off until <see cref="FundingRates"/> lists a rate (decision D-L3, same on every network); buying is always
/// available. We only accept <see cref="LiquidityPaymentType.FromChannelBalance"/> (decision D-L2). When we sell, our
/// rates go out in our <c>init</c> and our <c>node_announcement</c> (<c>option_will_fund</c>).
/// </remarks>
public class LiquidityAdsOptions
{
    /// <summary>The most rates <see cref="FundingRates"/> may list.</summary>
    public const int MaxFundingRates = 16;

    /// <summary>The default <see cref="MaxConcurrentSales"/>.</summary>
    public const int DefaultMaxConcurrentSales = 4;

    /// <summary>The default <see cref="MaxSalesPerPeer"/>.</summary>
    public const int DefaultMaxSalesPerPeer = 1;

    /// <summary>The largest <see cref="FundingRateOptions.FeeBasis"/>: 10,000 basis points are 100 %.</summary>
    public const ushort MaxFeeBasis = 10_000;

    private const ulong MaxMoneySat = 21_000_000UL * 100_000_000UL;

    /// <summary>
    /// The rates we sell at (<c>Node:LiquidityAds:FundingRates</c>), in the order a buyer sees them; empty (the default)
    /// means we do not sell.
    /// </summary>
    public List<FundingRateOptions> FundingRates { get; set; } = [];

    /// <summary>
    /// The most sale negotiations that hold our wallet inputs at once, node-wide (<c>Node:LiquidityAds:MaxConcurrentSales</c>,
    /// decision D-L5).
    /// </summary>
    public int MaxConcurrentSales { get; set; } = DefaultMaxConcurrentSales;

    /// <summary>
    /// The most sale negotiations one peer may have at once (<c>Node:LiquidityAds:MaxSalesPerPeer</c>, decision D-L5).
    /// </summary>
    public int MaxSalesPerPeer { get; set; } = DefaultMaxSalesPerPeer;

    /// <summary>
    /// How long, in blocks, we keep a channel we sold liquidity on open before we close it cooperatively ourselves
    /// (<c>Node:LiquidityAds:LeaseBlocks</c>, default <see cref="LiquidityAdsConstants.LeaseBlocks"/>, about a month;
    /// decision D-L4).
    /// </summary>
    public uint LeaseBlocks { get; set; } = LiquidityAdsConstants.LeaseBlocks;

    /// <summary>
    /// The default most we pay, in satoshis (mining and service fee together), for liquidity we buy when a request
    /// gives no limit of its own (<c>Node:LiquidityAds:MaxFeeSat</c>); null leaves the limit to each request.
    /// </summary>
    public ulong? MaxFeeSat { get; set; }

    /// <summary>Whether we sell liquidity (at least one rate is configured).</summary>
    public bool IsSelling => FundingRates is { Count: > 0 };

    /// <summary>
    /// Our <c>option_will_fund</c> rates for <c>init</c> and <c>node_announcement</c>: the configured rates with
    /// <see cref="LiquidityPaymentType.FromChannelBalance"/> as the only payment type, or null when we do not sell.
    /// </summary>
    public WillFundRates? GetWillFundRates()
    {
        if (!IsSelling)
            return null;

        return WillFundRates.Create(FundingRates.Select(r => r.ToFundingRate()).ToList(),
                                    [LiquidityPaymentType.FromChannelBalance]);
    }

    /// <summary>Returns every configuration error of these options; empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        var rates = FundingRates ?? [];
        if (rates.Count > MaxFundingRates)
            errors.Add($"LiquidityAds:{nameof(FundingRates)} lists {rates.Count} rates; at most {MaxFundingRates}.");

        for (var i = 0; i < rates.Count; i++)
        {
            var rate = rates[i];
            var name = $"LiquidityAds:{nameof(FundingRates)}:{i}";
            if (rate is null)
            {
                errors.Add($"{name} is empty.");
                continue;
            }

            if (rate.MaxAmountSat == 0)
                errors.Add($"{name}:{nameof(FundingRateOptions.MaxAmountSat)} must be positive.");
            if (rate.MinAmountSat > rate.MaxAmountSat)
                errors.Add($"{name}:{nameof(FundingRateOptions.MinAmountSat)} must be at most "
                         + $"{nameof(FundingRateOptions.MaxAmountSat)}.");
            if (rate.FeeBasis > MaxFeeBasis)
                errors.Add($"{name}:{nameof(FundingRateOptions.FeeBasis)} must be at most {MaxFeeBasis} basis points.");
        }

        if (MaxConcurrentSales <= 0)
            errors.Add($"LiquidityAds:{nameof(MaxConcurrentSales)} must be positive.");
        if (MaxSalesPerPeer <= 0)
            errors.Add($"LiquidityAds:{nameof(MaxSalesPerPeer)} must be positive.");
        else if (MaxConcurrentSales > 0 && MaxSalesPerPeer > MaxConcurrentSales)
            errors.Add($"LiquidityAds:{nameof(MaxSalesPerPeer)} must be at most {nameof(MaxConcurrentSales)}.");
        if (LeaseBlocks == 0)
            errors.Add($"LiquidityAds:{nameof(LeaseBlocks)} must be positive.");
        if (MaxFeeSat > MaxMoneySat)
            errors.Add($"LiquidityAds:{nameof(MaxFeeSat)} is more than 21 million bitcoin.");
        return errors;
    }
}

/// <summary>
/// One rate we sell at (an entry of <see cref="LiquidityAdsOptions.FundingRates"/>; the wire <c>funding_rate</c>,
/// <see cref="FundingRate"/>).
/// </summary>
public class FundingRateOptions
{
    /// <summary>The smallest amount, in satoshis, a buyer may buy at this rate.</summary>
    public uint MinAmountSat { get; set; }

    /// <summary>The largest amount, in satoshis, a buyer may buy at this rate.</summary>
    public uint MaxAmountSat { get; set; }

    /// <summary>The weight of our inputs and outputs whose mining fee the buyer refunds.</summary>
    public ushort FundingWeight { get; set; }

    /// <summary>The proportional fee, in basis points of the amount we contribute (at most 10,000).</summary>
    public ushort FeeBasis { get; set; }

    /// <summary>The flat fee, in satoshis, of every purchase.</summary>
    public uint FeeBaseSat { get; set; }

    /// <summary>The extra flat fee, in satoshis, when the purchase opens a new channel.</summary>
    public uint ChannelCreationFeeSat { get; set; }

    /// <summary>This rate as the wire <c>funding_rate</c>.</summary>
    public FundingRate ToFundingRate() =>
        new(MinAmountSat, MaxAmountSat, FundingWeight, FeeBasis, FeeBaseSat, ChannelCreationFeeSat);
}