namespace NLightning.Infrastructure.Bitcoin.Options;

using Domain.Node.Options;
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

    /// <summary>
    /// <see cref="SourceHttp"/>: the API. <c>https://</c>, or plain <c>http://</c> only to a loopback or <c>.onion</c>
    /// host unless <see cref="AllowPlainHttp"/> (NL-678); the answer is read up to 64 KiB.
    /// </summary>
    public string Url { get; set; } = "https://mempool.space/api/v1/fees/recommended";

    /// <summary>
    /// Allow a plain <c>http://</c> <see cref="Url"/> to any host (a fee server you trust on your own network). Default
    /// false: an unauthenticated fee rate could be raised or lowered by anyone on the path (NL-678).
    /// </summary>
    public bool AllowPlainHttp { get; set; }
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

    /// <summary>
    /// The feerate in sat/kw (at least 253) used while there is no estimate: before the first successful fetch, and
    /// while every fetch fails (bitcoind without <c>estimatesmartfee</c> data, an unreachable API). Once there was an
    /// estimate, a failed refresh keeps it instead.
    /// </summary>
    public uint FallbackFeeRatePerKw { get; set; } = 2_500;

    /// <summary>The cache file name the daemon's template writes (and uses when its file names none).</summary>
    public const string DefaultCacheFile = "fee_estimation_cache.bin";

    /// <summary>
    /// Where the last good estimate is saved (NL-706): written after every successful fetch, off the caller's path, and
    /// read at the start, so after a restart the node has a recent estimate instead of
    /// <see cref="FallbackFeeRatePerKw"/> while the first fetch is still running or failing (an API not reachable yet,
    /// Tor still building circuits). A relative path is taken from the working directory; the daemon anchors the
    /// file's relative path, or <see cref="DefaultCacheFile"/> when the file names none, to the configuration directory
    /// (NL-306). Empty (the library default) turns the cache off. <see cref="SourceFixed"/> never uses it.
    /// </summary>
    public string CacheFile { get; set; } = string.Empty;

    /// <summary>The <see cref="CacheExpiration"/> default (5 minutes).</summary>
    public static readonly TimeSpan DefaultCacheExpiration = TimeSpan.FromMinutes(5);

    /// <summary>The shortest <see cref="CacheExpiration"/> (10 s): a public fee API is not polled faster.</summary>
    public static readonly TimeSpan MinCacheExpiration = TimeSpan.FromSeconds(10);

    /// <summary>The longest <see cref="CacheExpiration"/> (1 day).</summary>
    public static readonly TimeSpan MaxCacheExpiration = TimeSpan.FromDays(1);

    /// <summary>The longest <see cref="CacheMaxAge"/> (7 days): an older estimate says nothing about today's fees.</summary>
    public static readonly TimeSpan MaxCacheMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// How long an estimate is fresh (<c>30s</c>, <c>5m</c>, <c>1h</c>, <c>1d</c>; default 5 minutes): the refresh
    /// interval, and a saved estimate younger than this is used at the start without waiting for a fetch. One number and
    /// one unit (<see cref="TryParseDuration"/>), from <see cref="MinCacheExpiration"/> to
    /// <see cref="MaxCacheExpiration"/>; anything else is refused at the start (NL-756: it silently became 5 minutes).
    /// </summary>
    public string CacheExpiration { get; set; } = "5m"; // 5 minutes

    /// <summary>
    /// The oldest saved estimate used at the start (same format as <see cref="CacheExpiration"/>; default 1 hour). An
    /// older one is ignored and logged: <see cref="FallbackFeeRatePerKw"/> applies until the first fetch succeeds.
    /// Checked only with a <see cref="CacheFile"/>: at least <see cref="CacheExpiration"/>, at most
    /// <see cref="MaxCacheMaxAge"/>.
    /// </summary>
    public string CacheMaxAge { get; set; } = "1h";

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
            if (HttpUrlPolicy.GetError(Url, AllowPlainHttp, "FeeEstimation:Url", "FeeEstimation:AllowPlainHttp") is
                { } urlError)
                errors.Add(urlError + ".");
            if (!FeeRateConverter.IsKnownUnit(RateUnit))
                errors.Add($"FeeEstimation:RateUnit '{RateUnit}' is not one of {FeeRateConverter.KnownUnitsText}.");
            // Anything but GET was sent as a POST (NL-756)
            if (!string.Equals(Method?.Trim(), "GET", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(Method?.Trim(), "POST", StringComparison.OrdinalIgnoreCase))
                errors.Add($"FeeEstimation:Method '{Method}' is not GET or POST.");
            if (string.IsNullOrWhiteSpace(PreferredFeeRate))
                errors.Add("FeeEstimation:PreferredFeeRate must name the JSON property that holds the rate.");
        }

        if (IsSource(SourceBitcoind))
        {
            if (ConfirmationTarget is < 1 or > 1008)
                errors.Add("FeeEstimation:ConfirmationTarget must be between 1 and 1008.");
            if (!EstimateMode.Equals("CONSERVATIVE", StringComparison.OrdinalIgnoreCase)
             && !EstimateMode.Equals("ECONOMICAL", StringComparison.OrdinalIgnoreCase))
                errors.Add($"FeeEstimation:EstimateMode '{EstimateMode}' is not CONSERVATIVE or ECONOMICAL.");
        }

        if (FallbackFeeRatePerKw < FeeRateConverter.FeeratePerKwFloor)
            errors.Add(
                $"FeeEstimation:FallbackFeeRatePerKw must be at least {FeeRateConverter.FeeratePerKwFloor} sat/kw.");

        // NL-756: a malformed or out-of-range value is refused, never read as the default
        var expirationValid = TryParseDuration(CacheExpiration, out var expiration);
        if (!expirationValid)
            errors.Add($"FeeEstimation:CacheExpiration '{CacheExpiration}' is not a duration such as 30s, 5m or 1h "
                     + "(one number and one unit: s, m, h or d).");
        else if (expiration < MinCacheExpiration || expiration > MaxCacheExpiration)
            errors.Add($"FeeEstimation:CacheExpiration '{CacheExpiration}' must be between 10s and 1d.");

        if (!string.IsNullOrWhiteSpace(CacheFile))
        {
            if (!TryParseDuration(CacheMaxAge, out var maxAge))
                errors.Add($"FeeEstimation:CacheMaxAge '{CacheMaxAge}' is not a positive duration such as 30m, 1h or "
                         + "1d.");
            else if (maxAge > MaxCacheMaxAge)
                errors.Add($"FeeEstimation:CacheMaxAge '{CacheMaxAge}' must be at most 7d.");
            else if (expirationValid && maxAge < expiration)
                errors.Add($"FeeEstimation:CacheMaxAge '{CacheMaxAge}' must not be shorter than "
                         + $"FeeEstimation:CacheExpiration '{CacheExpiration}'.");
        }

        if (IsSource(SourceFixed) && FixedFeeRatePerKw < FeeRateConverter.FeeratePerKwFloor)
            errors.Add($"FeeEstimation:FixedFeeRatePerKw must be at least {FeeRateConverter.FeeratePerKwFloor} sat/kw.");

        return errors;
    }

    /// <summary>
    /// Parses a positive duration written as a number and a unit: <c>s</c>/<c>second(s)</c>, <c>m</c>/<c>minute(s)</c>,
    /// <c>h</c>/<c>hour(s)</c> or <c>d</c>/<c>day(s)</c>, case-insensitive (<c>30s</c>, <c>5m</c>, <c>1h</c>).
    /// </summary>
    internal static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var trimmed = text?.Trim() ?? string.Empty;
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            digits++;

        if (digits == 0 || !int.TryParse(trimmed.AsSpan(0, digits), out var value) || value <= 0)
            return false;

        double? seconds = trimmed[digits..].Trim().ToLowerInvariant() switch
        {
            "s" or "second" or "seconds" => value,
            "m" or "minute" or "minutes" => value * 60.0,
            "h" or "hour" or "hours" => value * 3_600.0,
            "d" or "day" or "days" => value * 86_400.0,
            _ => null
        };
        if (seconds is not { } total || total >= TimeSpan.MaxValue.TotalSeconds)
            return false;

        duration = TimeSpan.FromSeconds(total);
        return true;
    }

    /// <summary>
    /// True when <see cref="Source"/> is <paramref name="source"/> (case-insensitive).
    /// </summary>
    public bool IsSource(string source) => string.Equals(Source?.Trim(), source, StringComparison.OrdinalIgnoreCase);
}