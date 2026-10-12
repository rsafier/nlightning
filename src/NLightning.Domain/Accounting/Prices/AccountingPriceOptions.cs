namespace NLightning.Domain.Accounting.Prices;

using Constants;
using Node.Options;

/// <summary>
/// The fiat valuation of the financial books (configuration section <see cref="SectionName"/>, NL-602 A3-T2, D-A1,
/// D-A11). Invalid options keep the back-valuation off (logged); they never stop the node.
/// </summary>
public sealed class AccountingPriceOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Accounting:Prices";

    /// <summary>The default <see cref="Url"/>: mempool.space's historical price API, the host the fee estimate already
    /// uses (D-A11); <c>?currency=&lt;code&gt;&amp;timestamp=&lt;unix seconds&gt;</c> is appended.</summary>
    public const string DefaultUrl = "https://mempool.space/api/v1/historical-price";

    /// <summary>The same API on mempool.space's onion service, the recommended <see cref="Url"/> with Tor on: its
    /// clearnet API refuses Tor exits (NL-868). Plain <c>http://</c> is allowed to a <c>.onion</c> host (NL-678).</summary>
    public const string MempoolOnionUrl =
        "http://mempoolhqx4isw62xs7abwphsq7ldayuidyx2v2oethdhhj6mlo2r6ad.onion/api/v1/historical-price";

    /// <summary>The default <see cref="CsvFile"/>, under the configuration directory (the daemon anchors a relative
    /// path there).</summary>
    public const string DefaultCsvFile = "prices.csv";

    /// <summary>The most hours one <c>prices fetch</c> asks for (31 days).</summary>
    public const int MaxFetchHoursPerCommand = 744;

    /// <summary>The base currency, an ISO 4217 code (D-A1: USD).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Which sources the job asks (default <see cref="AccountingPriceSourceMode.Both"/>: the file first).</summary>
    public AccountingPriceSourceMode Source { get; set; } = AccountingPriceSourceMode.Both;

    /// <summary>The default <see cref="MaxPriceJumpFactor"/>: a fetched price more than 3 times (or less than a third
    /// of) a stored neighbor is refused.</summary>
    public const decimal DefaultMaxPriceJumpFactor = 3m;

    /// <summary>The largest <see cref="MaxPriceJumpFactor"/> (10^6).</summary>
    public const decimal MaxPriceJumpFactorLimit = 1_000_000m;

    /// <summary>The HTTP source's endpoint (<see cref="DefaultUrl"/>; with Tor on <see cref="MempoolOnionUrl"/> is
    /// recommended, NL-868); requests go through Tor whenever <c>Node:Tor:Mode</c> is not <c>Off</c> (NL-677) unless
    /// <see cref="ThroughTor"/> is false. <c>https://</c>, or <c>http://</c> only to a loopback or
    /// <c>.onion</c> host unless <see cref="AllowPlainHttp"/> (NL-678).</summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>
    /// Whether the HTTP source's requests go through Tor (NL-868): unset (default) = whenever <c>Node:Tor:Mode</c> is
    /// not <c>Off</c> (NL-677); true = through Tor, refused at start with Tor <c>Off</c>; false = directly from the
    /// node's IP in <c>Hybrid</c> too (a <c>.onion</c> <see cref="Url"/> still goes through Tor), refused at start in
    /// <c>TorOnly</c>, which sends every connection through Tor. Privacy trade-off of false: the hours asked mark when
    /// the node moved money (SECURITY_REVIEW SR-21), and mempool.space then sees them from the node's IP; prefer
    /// <see cref="MempoolOnionUrl"/> through Tor, since the clearnet API refuses Tor exits.
    /// </summary>
    public bool? ThroughTor { get; set; }

    /// <summary>Allow a plain <c>http://</c> <see cref="Url"/> to any host (a price server you trust on your own
    /// network); default false: plain HTTP only to loopback or <c>.onion</c> hosts (NL-678).</summary>
    public bool AllowPlainHttp { get; set; }

    /// <summary>
    /// The sanity bound of a fetched (HTTP) price (NL-678): one that differs from the nearest stored price within
    /// <see cref="MaxAge"/> (before or after it) by more than this factor, either way, is refused and never stored (a
    /// decimal-point, unit or currency mistake of the source; a stored price is never replaced). Default
    /// <see cref="DefaultMaxPriceJumpFactor"/>; 0 turns the check off; otherwise more than 1 and at most
    /// <see cref="MaxPriceJumpFactorLimit"/>. Imported prices and the operator's price file are trusted as given.
    /// </summary>
    public decimal MaxPriceJumpFactor { get; set; } = DefaultMaxPriceJumpFactor;

    /// <summary>The operator's price file (<c>unixSeconds,price</c> per line, D-A11); read again when it changes.</summary>
    public string CsvFile { get; set; } = DefaultCsvFile;

    /// <summary>A posting takes the nearest price at or before its time no older than this (26 h), else it stays
    /// unvalued (D-A11).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(26);

    /// <summary>How often the back-valuation job runs (10 min).</summary>
    public TimeSpan FetchInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The most prices one round of the job asks the sources for (24); 0 = value with stored prices only.</summary>
    public int MaxFetchesPerRound { get; set; } = 24;

    /// <summary>The normalized currency code (upper case).</summary>
    public string NormalizedCurrency => (Currency ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Whether the job may ask a source (<see cref="Source"/> not <see cref="AccountingPriceSourceMode.None"/>).</summary>
    public bool HasSource => Source != AccountingPriceSourceMode.None;

    /// <summary>Whether the price file is asked.</summary>
    public bool UsesCsv => Source is AccountingPriceSourceMode.Csv or AccountingPriceSourceMode.Both;

    /// <summary>Whether the HTTP source is asked.</summary>
    public bool UsesHttp => Source is AccountingPriceSourceMode.Http or AccountingPriceSourceMode.Both;

    /// <summary>The problems of the options; empty when they are valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (!IsCurrencyCode(NormalizedCurrency))
            errors.Add($"{SectionName}:Currency '{Currency}' is not a three-letter ISO 4217 code");
        if (!Enum.IsDefined(Source))
            errors.Add($"{SectionName}:Source '{Source}' is not None, Csv, Http or Both");
        if (UsesHttp
            && HttpUrlPolicy.GetError(Url, AllowPlainHttp, $"{SectionName}:Url",
                                      $"{SectionName}:{nameof(AllowPlainHttp)}") is { } urlError)
            errors.Add(urlError);
        if (MaxPriceJumpFactor != 0 && (MaxPriceJumpFactor <= 1 || MaxPriceJumpFactor > MaxPriceJumpFactorLimit))
            errors.Add($"{SectionName}:MaxPriceJumpFactor must be 0 (off) or more than 1 and at most "
                     + $"{MaxPriceJumpFactorLimit}");
        if (UsesCsv && string.IsNullOrWhiteSpace(CsvFile))
            errors.Add($"{SectionName}:CsvFile is empty");
        if (MaxAge <= TimeSpan.Zero)
            errors.Add($"{SectionName}:MaxAge must be positive");
        if (FetchInterval <= TimeSpan.Zero)
            errors.Add($"{SectionName}:FetchInterval must be positive");
        if (MaxFetchesPerRound < 0)
            errors.Add($"{SectionName}:MaxFetchesPerRound must not be negative");

        return errors;
    }

    /// <summary>
    /// Whether the HTTP source's requests go through Tor under <paramref name="tor"/> (NL-868): never with Tor
    /// <c>Off</c>, always in <c>TorOnly</c>, else <see cref="ThroughTor"/> (unset = true, NL-677). A <c>.onion</c>
    /// <see cref="Url"/> goes through Tor whenever Tor is on.
    /// </summary>
    public bool RoutesThroughTor(TorOptions? tor) =>
        tor is { IsEnabled: true } && (tor.IsTorOnly || ThroughTor != false);

    /// <summary>
    /// The start-up errors of <see cref="ThroughTor"/> against <c>Node:Tor</c> (NL-868; empty when valid or when the
    /// HTTP source is not used): true with Tor <c>Off</c> (there is no Tor to go through), false in <c>TorOnly</c>
    /// (every connection goes through Tor). Never a silent change of route: the host refuses to start.
    /// </summary>
    public IReadOnlyList<string> GetTorRoutingErrors(TorOptions? tor)
    {
        var errors = new List<string>();
        if (!UsesHttp || ThroughTor is null)
            return errors;

        if (ThroughTor == true && tor is not { IsEnabled: true })
            errors.Add($"{SectionName}:{nameof(ThroughTor)} is true but Node:Tor:Mode is Off: there is no Tor to send "
                     + $"the price requests through; turn Tor on, or unset {SectionName}:{nameof(ThroughTor)}");
        if (ThroughTor == false && tor is { IsEnabled: true, IsTorOnly: true })
            errors.Add($"{SectionName}:{nameof(ThroughTor)} is false but Node:Tor:Mode is TorOnly, which sends every "
                     + $"connection through Tor: unset {SectionName}:{nameof(ThroughTor)} (with Tor, "
                     + $"{SectionName}:Url {MempoolOnionUrl} is recommended)");

        return errors;
    }

    /// <summary>
    /// Whether a fetched <paramref name="price"/> is within <see cref="MaxPriceJumpFactor"/> of a stored neighbor
    /// <paramref name="reference"/> (both positive); always true with the check off.
    /// </summary>
    public bool IsPlausibleNext(decimal price, decimal reference)
    {
        if (MaxPriceJumpFactor == 0)
            return true;
        if (price <= 0 || reference <= 0)
            return false;

        // price / reference within [1 / factor, factor], without dividing (no overflow at the schema's bounds)
        return price <= reference * MaxPriceJumpFactor && price * MaxPriceJumpFactor >= reference;
    }

    /// <summary>Whether <paramref name="code"/> is three upper-case ASCII letters.</summary>
    public static bool IsCurrencyCode(string? code) =>
        code is { Length: AccountingSchemaLimits.CurrencyLength } && code.All(char.IsAsciiLetterUpper);
}