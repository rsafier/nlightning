namespace NLightning.Infrastructure.Bitcoin.Options;

public class FeeEstimationOptions
{
    public string Url { get; set; } = "https://mempool.space/api/v1/fees/recommended";
    public string Method { get; set; } = "GET";
    public string Body { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/json";
    public string PreferredFeeRate { get; set; } = "fastestFee";
    public string CacheFile { get; set; } = "fee_estimation_cache.bin";
    public string CacheExpiration { get; set; } = "5m"; // 5 minutes

    /// <summary>
    /// Converts the estimator's unit to sat/kw (NL-288). mempool.space answers in sat/vB, and 1 vbyte = 4 weight
    /// units, so 1 sat/vB = 250 sat/kw. The old default 1000 gave sat/kvB,
    /// four times too high; a config file written by an older build still carries it and must be changed by hand.
    /// </summary>
    public string RateMultiplier { get; set; } = "250";
}