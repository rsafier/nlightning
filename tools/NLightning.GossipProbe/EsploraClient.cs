using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NLightning.GossipProbe;

/// <summary>
/// A polite Esplora (mempool.space) reader: at most the given requests per second, and a stop on HTTP 429.
/// </summary>
public sealed class EsploraClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly TimeSpan _interval;
    private DateTime _next = DateTime.MinValue;

    public EsploraClient(string baseUrl, double requestsPerSecond)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _interval = TimeSpan.FromSeconds(1 / Math.Max(0.1, requestsPerSecond));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("nltg-gossip-probe/0.1 (test harness)");
    }

    /// <summary>Requests made so far.</summary>
    public int Requests { get; private set; }

    /// <summary>Reads the chain tip height (one request, no rate state).</summary>
    public static async Task<uint> GetTipHeightAsync(string baseUrl)
    {
        using var client = new EsploraClient(baseUrl, 1);
        return uint.Parse(await client.GetStringAsync("/blocks/tip/height"), CultureInfo.InvariantCulture);
    }

    /// <summary>GET <paramref name="path"/> as text; null on 404.</summary>
    /// <exception cref="RateLimitedException">HTTP 429.</exception>
    public async Task<string?> TryGetStringAsync(string path, CancellationToken cancellationToken = default)
    {
        var wait = _next - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellationToken);
        _next = DateTime.UtcNow + _interval;
        Requests++;

        using var response = await _http.GetAsync(_baseUrl + path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
    }

    public async Task<string> GetStringAsync(string path, CancellationToken cancellationToken = default) =>
        await TryGetStringAsync(path, cancellationToken) ?? throw new HttpRequestException($"{path}: not found");

    public async Task<JsonElement?> TryGetJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await TryGetStringAsync(path, cancellationToken);
        return text is null ? null : JsonDocument.Parse(text).RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();

    public sealed class RateLimitedException() : Exception("HTTP 429 from the Esplora API");
}