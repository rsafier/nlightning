using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Eclair;

/// <summary>
/// Eclair's JSON API (HTTP basic auth with an empty user; every call a form-encoded POST to <c>/&lt;method&gt;</c>), at
/// an address that can change: <see cref="Retarget"/> after a restart moves it to the new pod's IP.
/// </summary>
public sealed class EclairApi : IDisposable
{
    private readonly HttpClient _http;
    private Uri _baseAddress;

    public EclairApi(Uri baseAddress, string password, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        _baseAddress = baseAddress;
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{password}")));
    }

    /// <summary>Where the calls go (<c>http://host:8080/</c>).</summary>
    public Uri BaseAddress => Volatile.Read(ref _baseAddress);

    /// <summary>Sends the following calls to <paramref name="baseAddress"/> (the node's new address after a restart).</summary>
    public void Retarget(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        Volatile.Write(ref _baseAddress, baseAddress);
    }

    /// <summary>The API address of <paramref name="host"/> (an IPv6 literal gets brackets).</summary>
    public static Uri BuildBaseAddress(string host, int port = EclairNode.ApiPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var literal = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        return new Uri($"http://{literal}:{port.ToString(CultureInfo.InvariantCulture)}/");
    }

    /// <summary>The form value of an argument (booleans lower case, numbers invariant).</summary>
    public static string FormatArgument(object value) =>
        value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    /// <summary>Calls <paramref name="method"/> with <paramref name="args"/> (null values are left out).</summary>
    /// <exception cref="EclairApiException">Eclair answered with an error.</exception>
    public async Task<JsonNode?> CallAsync(string method, CancellationToken cancellationToken,
                                           params (string Name, object? Value)[] args)
    {
        var form = args.Where(a => a.Value is not null)
                       .Select(a => new KeyValuePair<string, string>(a.Name, FormatArgument(a.Value!)));
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(new Uri(BaseAddress, method), content, cancellationToken)
                                        .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new EclairApiException(method, (int)response.StatusCode, ReadError(body));

        return ParseBody(body);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A successful answer: JSON, or a plain string for the calls that answer with text (open, close).</summary>
    public static JsonNode? ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body.Trim().Trim('"'));
        }
    }

    private static string ReadError(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?.ToString() ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}

/// <summary>An error answer of Eclair's API.</summary>
public sealed class EclairApiException(string method, int statusCode, string message)
    : Exception($"eclair {method} failed ({statusCode}): {message}")
{
    public string Method { get; } = method;

    public int StatusCode { get; } = statusCode;

    public string EclairMessage { get; } = message;
}