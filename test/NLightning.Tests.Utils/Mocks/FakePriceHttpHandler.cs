using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// A mempool.space historical-price API in memory (NL-602 A3-T2): answers
/// <c>GET ...?currency=X&amp;timestamp=t</c> with the hourly point at or before <c>t</c> (<see cref="PriceAt"/>), and
/// records every request so a test can prove there was none.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FakePriceHttpHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<Uri> _requests = [];

    /// <summary>The price of the hour starting at the given Unix second, or null for "no data" (an empty array).</summary>
    public Func<long, decimal?> PriceAt { get; set; } = _ => 86_048m;

    /// <summary>When set, the status every request answers instead.</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>The requests, in order.</summary>
    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                           CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _requests.Add(request.RequestUri!);

        if (FailWith is { } status)
            return Task.FromResult(new HttpResponseMessage(status));

        var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                           .Select(p => p.Split('=', 2))
                           .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var currency = query["currency"];
        var timestamp = long.Parse(query["timestamp"], System.Globalization.CultureInfo.InvariantCulture);
        var hour = timestamp - timestamp % 3600;
        var price = PriceAt(hour);
        var body = price is { } value
                       ? $"{{\"prices\":[{{\"time\":{hour},\"{currency}\":"
                       + $"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}],"
                       + "\"exchangeRates\":{\"USDEUR\":0.9}}"
                       : "{\"prices\":[],\"exchangeRates\":{}}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}