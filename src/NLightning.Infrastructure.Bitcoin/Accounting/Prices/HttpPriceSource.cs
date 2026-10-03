using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Accounting.Prices;

using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;
using Domain.Node.Options;
using Infrastructure.Transport.Http;

/// <summary>
/// mempool.space's historical price API as a price source (D-A11, NL-602 A3-T2):
/// <c>GET {Url}?currency=USD&amp;timestamp=&lt;unix seconds&gt;</c> answers
/// <c>{"prices":[{"time":t,"USD":p,...}],"exchangeRates":{"USDEUR":r,...}}</c> with the hourly price point at or
/// before the time. A currency the point does not carry is converted from USD through <c>exchangeRates</c>
/// (<c>USD&lt;code&gt;</c>). Only the back-valuation job and <c>prices fetch</c> ask it; its <see cref="HttpClient"/>
/// is built next to the fee service's and goes through Tor whenever Tor is on (NL-677) unless
/// <c>Accounting:Prices:ThroughTor</c> is false (NL-868). The answer is read up to <see cref="MaxResponseBytes"/>
/// (NL-678). A failure (network, status, body, size) answers null, is logged at Debug and kept as
/// <see cref="LastFailure"/>: the back-valuation sums a round's failures in one warning (NL-868). The first failure of
/// mempool.space's clearnet API through Tor logs one hint (its onion URL, or <c>ThroughTor</c> false). The stored
/// prices are the cache, so a price is asked once.
/// </summary>
public sealed class HttpPriceSource : IPriceSource
{
    /// <summary>The longest answer read (64 KiB, NL-678): a historical-price answer is a few hundred bytes.</summary>
    public const int MaxResponseBytes = HttpResponseLimits.SmallResponseMaxBytes;

    private const string UsdCode = "USD";
    private const string MempoolClearnetHost = "mempool.space";

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpPriceSource> _logger;
    private readonly AccountingPriceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TorMode? _torRoute;
    private int _torHintLogged;

    /// <param name="httpClient">The client (built by <c>AddAccountingPriceSources</c>).</param>
    /// <param name="options">The <c>Accounting:Prices</c> options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of <see cref="AccountingPrice.FetchedAt"/>.</param>
    /// <param name="torRoute">The Tor mode when the client's clearnet requests go through Tor (null when they go
    /// directly); only chooses the hint of the first failure.</param>
    public HttpPriceSource(HttpClient httpClient, IOptions<AccountingPriceOptions> options,
                           ILogger<HttpPriceSource> logger, TimeProvider? timeProvider = null, TorMode? torRoute = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _torRoute = torRoute;
    }

    /// <inheritdoc />
    public string? LastFailure { get; private set; }

    /// <summary>The Tor mode its clearnet requests go through Tor under; null when they go directly (NL-868).</summary>
    public TorMode? TorRoute => _torRoute;

    /// <summary>The request URL for <paramref name="currency"/> at <paramref name="time"/>.</summary>
    public Uri BuildRequestUri(string currency, DateTimeOffset time)
    {
        var separator = _options.Url.Contains('?') ? '&' : '?';
        return new Uri(string.Create(CultureInfo.InvariantCulture,
                                     $"{_options.Url}{separator}currency={Uri.EscapeDataString(currency)}"
                                   + $"&timestamp={time.ToUnixTimeSeconds()}"));
    }

    /// <inheritdoc />
    public async Task<AccountingPrice?> GetPriceAsync(string currency, DateTimeOffset time,
                                                      CancellationToken cancellationToken = default)
    {
        LastFailure = null;
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!AccountingPriceOptions.IsCurrencyCode(code))
            return null;

        Uri uri;
        try
        {
            uri = BuildRequestUri(code, time);
        }
        catch (UriFormatException e)
        {
            _logger.LogWarning(e, "The price source URL {Url} is not valid", _options.Url);
            LastFailure = $"the URL {_options.Url} is not valid";
            return null;
        }

        // HttpClient.Timeout stops at the headers with ResponseHeadersRead: one deadline covers the request and the
        // bounded body read, so a stalled body times out like a request (NL-732)
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpClient.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(_httpClient.Timeout);

        try
        {
            // The body is read bounded (NL-678), not buffered whole by the client
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                                                            deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"the price source answered {(int)response.StatusCode}", code, time);
            }

            var body = await HttpResponseLimits.ReadBoundedStringAsync(response.Content, MaxResponseBytes,
                                                                       deadline.Token);
            if (TryParse(body, code, out var priceTime, out var price, out var error))
                return new AccountingPrice(0, code, priceTime, price, AccountingPriceSource.Http,
                                           _timeProvider.GetUtcNow());

            return Fail($"the price source's answer has no price: {error}", code, time);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException
                                      or InvalidOperationException)
        {
            // A cancellation without ours is the client's timeout or the deadline of the body read (NL-732)
            return Fail($"the price source could not be reached: {e.Message}", code, time);
        }
    }

    /// <summary>
    /// Records a failure (<see cref="LastFailure"/>, Debug; the back-valuation warns once per round, NL-868) and, the
    /// first time mempool.space's clearnet API fails through Tor, logs the hint: its clearnet API refuses Tor exits.
    /// </summary>
    private AccountingPrice? Fail(string failure, string currency, DateTimeOffset time)
    {
        LastFailure = failure;
        _logger.LogDebug("No {Currency} price for {Time:O}: {Failure}", currency, time, failure);
        if (_torRoute is { } mode && IsMempoolClearnet(_options.Url) && Interlocked.Exchange(ref _torHintLogged, 1) == 0)
        {
            if (mode == TorMode.TorOnly)
                _logger.LogInformation(
                    "The price source {Url} failed through Tor: mempool.space's clearnet API often refuses Tor exits. "
                  + "Set {Section}:Url to its onion service {OnionUrl} (NL-868)", _options.Url,
                    AccountingPriceOptions.SectionName, AccountingPriceOptions.MempoolOnionUrl);
            else
                _logger.LogInformation(
                    "The price source {Url} failed through Tor: mempool.space's clearnet API often refuses Tor exits. "
                  + "Set {Section}:Url to its onion service {OnionUrl} (recommended), or {Section}:ThroughTor false to "
                  + "ask it directly (the hours asked then show from this node's IP when it moved money, NL-677, "
                  + "NL-868)", _options.Url, AccountingPriceOptions.SectionName, AccountingPriceOptions.MempoolOnionUrl,
                    AccountingPriceOptions.SectionName);
        }

        return null;
    }

    private static bool IsMempoolClearnet(string url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
     && uri.Host.TrimEnd('.').Equals(MempoolClearnetHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a historical-price answer: the first point's <c>time</c> and its price in <paramref name="currency"/>
    /// (directly, or USD times <c>exchangeRates.USD&lt;code&gt;</c>), rounded to 8 places; a price that is not
    /// positive (the API's -1 or 0 for unknown) is none.
    /// </summary>
    public static bool TryParse(string body, string currency, out DateTimeOffset time, out decimal price,
                                out string? error)
    {
        time = default;
        price = 0;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array
                || prices.GetArrayLength() == 0)
            {
                error = "no 'prices' array";
                return false;
            }

            var point = prices[0];
            if (point.ValueKind != JsonValueKind.Object
                || !point.TryGetProperty("time", out var timeElement)
                || !timeElement.TryGetInt64(out var seconds)
                || seconds < AccountingPriceCsv.EarliestTime.ToUnixTimeSeconds()
                || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                error = "no valid 'time'";
                return false;
            }

            time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (TryGetPositive(point, currency, out price))
            {
                error = null;
                return Round(ref price, out error);
            }

            if (currency != UsdCode
                && TryGetPositive(point, UsdCode, out var usd)
                && root.TryGetProperty("exchangeRates", out var rates) && rates.ValueKind == JsonValueKind.Object
                && TryGetPositive(rates, UsdCode + currency, out var rate))
            {
                price = usd * rate;
                return Round(ref price, out error);
            }

            error = $"no positive '{currency}' price";
            return false;
        }
        catch (Exception e) when (e is JsonException or OverflowException or ArgumentException)
        {
            error = e.Message;
            return false;
        }
    }

    private static bool TryGetPositive(JsonElement element, string name, out decimal value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out value) && value > 0;
    }

    private static bool Round(ref decimal price, out string? error)
    {
        price = Math.Round(price, AccountingSchemaLimits.FiatScale, MidpointRounding.ToEven);
        if (price <= 0 || price > AccountingPriceCsv.MaxPrice)
        {
            error = "the price is out of range";
            return false;
        }

        error = null;
        return true;
    }
}