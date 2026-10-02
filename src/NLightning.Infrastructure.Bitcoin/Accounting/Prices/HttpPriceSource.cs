using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Accounting.Prices;

using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;
using Infrastructure.Transport.Http;

/// <summary>
/// mempool.space's historical price API as a price source (D-A11, NL-602 A3-T2):
/// <c>GET {Url}?currency=USD&amp;timestamp=&lt;unix seconds&gt;</c> answers
/// <c>{"prices":[{"time":t,"USD":p,...}],"exchangeRates":{"USDEUR":r,...}}</c> with the hourly price point at or
/// before the time. A currency the point does not carry is converted from USD through <c>exchangeRates</c>
/// (<c>USD&lt;code&gt;</c>). Only the back-valuation job and <c>prices fetch</c> ask it; its <see cref="HttpClient"/>
/// is built next to the fee service's and goes through Tor whenever Tor is on (NL-677). The answer is read up to
/// <see cref="MaxResponseBytes"/> (NL-678). A failure (network, status, body, size) is logged and answers null; the
/// stored prices are the cache, so a price is asked once.
/// </summary>
public sealed class HttpPriceSource : IPriceSource
{
    /// <summary>The longest answer read (64 KiB, NL-678): a historical-price answer is a few hundred bytes.</summary>
    public const int MaxResponseBytes = HttpResponseLimits.SmallResponseMaxBytes;

    private const string UsdCode = "USD";

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpPriceSource> _logger;
    private readonly AccountingPriceOptions _options;
    private readonly TimeProvider _timeProvider;

    public HttpPriceSource(HttpClient httpClient, IOptions<AccountingPriceOptions> options,
                           ILogger<HttpPriceSource> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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
                _logger.LogWarning("The price source answered {Status} for {Currency} at {Time:O}",
                                   (int)response.StatusCode, code, time);
                return null;
            }

            var body = await HttpResponseLimits.ReadBoundedStringAsync(response.Content, MaxResponseBytes,
                                                                       deadline.Token);
            if (TryParse(body, code, out var priceTime, out var price, out var error))
                return new AccountingPrice(0, code, priceTime, price, AccountingPriceSource.Http,
                                           _timeProvider.GetUtcNow());

            _logger.LogWarning("The price source's answer for {Currency} at {Time:O} has no price: {Error}", code,
                               time, error);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException
                                      or InvalidOperationException)
        {
            // A cancellation without ours is the client's timeout or the deadline of the body read (NL-732)
            _logger.LogWarning("The price source could not be reached for {Currency} at {Time:O}: {Message}", code,
                               time, e.Message);
            return null;
        }
    }

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