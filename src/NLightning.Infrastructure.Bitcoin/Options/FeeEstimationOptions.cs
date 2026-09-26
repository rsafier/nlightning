namespace NLightning.Infrastructure.Bitcoin.Options;

using Services;

/// <summary>
/// Where the node's fee estimate comes from (configuration section <c>FeeEstimation</c>). Every source ends up in sat/kw
/// (BOLT 2/3 <c>feerate_per_kw</c>), never below the 253 sat/kw floor (see <see cref="FeeRateConverter"/>).
/// </summary>
public class FeeEstimationOptions
{
    /// <summary><see cref="Source"/> value: fetch <see cref="Url"/> (a mempool.space-style JSON API).</summary>
    public const string SourceHttp = "Http";

    /// <summary><see cref="Source"/> value: bitcoind's <c>estimatesmartfee</c> over the <c>Bitcoin</c> RPC settings.</summary>
    public const string SourceBitcoind = "Bitcoind";

    /// <summary><see cref="Source"/> value: always <see cref="FixedFeeRatePerKw"/>.</summary>
    public const string SourceFixed = "Fixed";

    /// <summary>
    /// <see cref="SourceHttp"/> (default), <see cref="SourceBitcoind"/> or <see cref="SourceFixed"/>, case-insensitive.
    /// </summary>
    public string Source { get; set; } = SourceHttp;

    public string Url { get; set; } = "https://mempool.space/api/v1/fees/recommended";
    public string Method { get; set; } = "GET";
    public string Body { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// The JSON property of the HTTP response that holds the rate (mempool.space: <c>fastestFee</c>,
    /// <c>halfHourFee</c>, <c>hourFee</c>, <c>economyFee</c>, <c>minimumFee</c>).
    /// </summary>
    public string PreferredFeeRate { get; set; } = "fastestFee";

    /// <summary>
    /// The unit of the HTTP value: <c>sat/vB</c> (default, mempool.space), <c>sat/kvB</c>, <c>sat/kw</c> or
    /// <c>BTC/kvB</c> (bitcoind's unit). Replaces <see cref="RateMultiplier"/> (NL-288).
    /// </summary>
    public string RateUnit { get; set; } = FeeRateConverter.SatPerVByte;

    /// <summary>
    /// Ignored since NL-288 (a multiplier of 1000 turned sat/vB into sat/kvB, not sat/kw, so every estimate was 4x too
    /// high). Kept so old configuration files still bind; the fee service logs a warning when it is set. Use
    /// <see cref="RateUnit"/>.
    /// </summary>
    public string? RateMultiplier { get; set; }

    /// <summary>
    /// <see cref="SourceBitcoind"/>: the confirmation target in blocks passed to <c>estimatesmartfee</c>.
    /// </summary>
    public int ConfirmationTarget { get; set; } = 6;

    /// <summary>
    /// <see cref="SourceBitcoind"/>: <c>CONSERVATIVE</c> (default) or <c>ECONOMICAL</c>.
    /// </summary>
    public string EstimateMode { get; set; } = "CONSERVATIVE";

    /// <summary>
    /// <see cref="SourceFixed"/>: the feerate in sat/kw (at least 253).
    /// </summary>
    public uint FixedFeeRatePerKw { get; set; } = 2_500;

    public string CacheFile { get; set; } = "fee_estimation_cache.bin";
    public string CacheExpiration { get; set; } = "5m"; // 5 minutes

    /// <summary>
    /// Returns every configuration error; empty when valid.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (!IsSource(SourceHttp) && !IsSource(SourceBitcoind) && !IsSource(SourceFixed))
            errors.Add($"FeeEstimation:Source '{Source}' is not {SourceHttp}, {SourceBitcoind} or {SourceFixed}.");

        if (IsSource(SourceHttp))
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out _))
                errors.Add($"FeeEstimation:Url '{Url}' is not an absolute URL.");
            if (!FeeRateConverter.IsKnownUnit(RateUnit))
                errors.Add($"FeeEstimation:RateUnit '{RateUnit}' is not one of {FeeRateConverter.KnownUnitsText}.");
        }

        if (IsSource(SourceBitcoind))
        {
            if (ConfirmationTarget is < 1 or > 1008)
                errors.Add("FeeEstimation:ConfirmationTarget must be between 1 and 1008.");
            if (!EstimateMode.Equals("CONSERVATIVE", StringComparison.OrdinalIgnoreCase)
             && !EstimateMode.Equals("ECONOMICAL", StringComparison.OrdinalIgnoreCase))
                errors.Add($"FeeEstimation:EstimateMode '{EstimateMode}' is not CONSERVATIVE or ECONOMICAL.");
        }

        if (IsSource(SourceFixed) && FixedFeeRatePerKw < FeeRateConverter.FeeratePerKwFloor)
            errors.Add($"FeeEstimation:FixedFeeRatePerKw must be at least {FeeRateConverter.FeeratePerKwFloor} sat/kw.");

        return errors;
    }

    /// <summary>
    /// True when <see cref="Source"/> is <paramref name="source"/> (case-insensitive).
    /// </summary>
    public bool IsSource(string source) => string.Equals(Source?.Trim(), source, StringComparison.OrdinalIgnoreCase);
}