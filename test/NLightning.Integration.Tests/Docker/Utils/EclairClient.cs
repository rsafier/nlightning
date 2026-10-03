using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// Eclair's JSON API (<c>eclair.api</c>, HTTP basic auth with an empty user) as the interop tests use it: every call is
/// a form-encoded POST to <c>/&lt;method&gt;</c>. Only the calls the proofs need (NL-180).
/// </summary>
public sealed class EclairClient : IDisposable
{
    private readonly HttpClient _http;
    private Uri _baseAddress;

    /// <summary>Eclair's API published on <c>127.0.0.1:<paramref name="apiPort"/></c> (Docker).</summary>
    public EclairClient(int apiPort, string password)
        : this(new Uri($"http://127.0.0.1:{apiPort}/"), password)
    {
    }

    /// <summary>Eclair's API at <paramref name="baseAddress"/> (the pod's address on the cluster backend).</summary>
    public EclairClient(Uri baseAddress, string password)
    {
        _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{password}")));
    }

    /// <summary>Where the calls go.</summary>
    public Uri BaseAddress => Volatile.Read(ref _baseAddress);

    /// <summary>
    /// Sends the following calls to <paramref name="baseAddress"/>: a restarted Eclair pod has another IP (cluster
    /// backend); Docker keeps the published port.
    /// </summary>
    public void Retarget(Uri baseAddress) =>
        Volatile.Write(ref _baseAddress, baseAddress ?? throw new ArgumentNullException(nameof(baseAddress)));

    /// <summary>
    /// Calls <paramref name="method"/> with <paramref name="args"/> (null values are left out).
    /// </summary>
    /// <exception cref="EclairRpcException">Eclair answered with an error.</exception>
    public async Task<JsonNode?> CallAsync(string method, CancellationToken cancellationToken,
                                           params (string Name, object? Value)[] args)
    {
        var form = args.Where(a => a.Value is not null)
                       .Select(a => new KeyValuePair<string, string>(a.Name, a.Value switch
                       {
                           bool b => b ? "true" : "false",
                           IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                           _ => a.Value!.ToString()!
                       }));
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(new Uri(BaseAddress, method), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string message;
            try
            {
                message = JsonNode.Parse(body)?["error"]?.ToString() ?? body;
            }
            catch
            {
                message = body;
            }

            throw new EclairRpcException(method, (int)response.StatusCode, message);
        }

        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            // Some calls (open, close) answer with a plain JSON string or text
            return JsonValue.Create(body.Trim().Trim('"'));
        }
    }

    public async Task<JsonNode> GetInfoAsync(CancellationToken cancellationToken) =>
        await CallAsync("getinfo", cancellationToken) ?? throw new EclairRpcException("getinfo", 0, "empty answer");

    public async Task<long> GetBlockHeightAsync(CancellationToken cancellationToken) =>
        (await GetInfoAsync(cancellationToken))["blockHeight"]!.GetValue<long>();

    public async Task ConnectAsync(string uri, CancellationToken cancellationToken) =>
        await CallAsync("connect", cancellationToken, ("uri", uri));

    public async Task DisconnectAsync(string nodeId, CancellationToken cancellationToken) =>
        await CallAsync("disconnect", cancellationToken, ("nodeId", nodeId));

    public async Task<JsonArray> PeersAsync(CancellationToken cancellationToken) =>
        (await CallAsync("peers", cancellationToken))?.AsArray() ?? [];

    /// <summary>
    /// Our node in Eclair's <c>peers</c>, or null.
    /// </summary>
    public async Task<JsonNode?> GetPeerAsync(string nodeId, CancellationToken cancellationToken) =>
        (await PeersAsync(cancellationToken)).FirstOrDefault(p => p?["nodeId"]?.GetValue<string>() == nodeId);

    public async Task<bool> IsConnectedAsync(string nodeId, CancellationToken cancellationToken) =>
        (await GetPeerAsync(nodeId, cancellationToken))?["state"]?.GetValue<string>() == "CONNECTED";

    /// <summary>
    /// <c>open</c>: a channel of <paramref name="fundingSat"/> to <paramref name="nodeId"/>; returns Eclair's answer
    /// (<c>created channel &lt;id&gt; with fundingTxId=... and fees=...</c>).
    /// </summary>
    public async Task<string> OpenAsync(string nodeId, long fundingSat, CancellationToken cancellationToken,
                                        long? pushMsat = null, string channelType = "anchor_outputs_zero_fee_htlc_tx",
                                        bool announce = false, long? fundingFeerateSatByte = null,
                                        long? fundingFeeBudgetSat = 10_000)
    {
        // fundingFeeBudgetSatoshis: Eclair's default budget is a small share of the funding amount (500 sat for a
        // 500k sat open), below its own mining fee once the peer contributes inputs to a dual-funded open
        var answer = await CallAsync("open", cancellationToken, ("nodeId", nodeId), ("fundingSatoshis", fundingSat),
                                     ("pushMsat", pushMsat), ("channelType", channelType),
                                     ("announceChannel", announce),
                                     ("fundingFeerateSatByte", fundingFeerateSatByte),
                                     ("fundingFeeBudgetSatoshis", fundingFeeBudgetSat), ("openTimeoutSeconds", 60));
        return answer?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// <c>channel</c>: the channel's state and data, or null when Eclair does not know it.
    /// </summary>
    public async Task<JsonNode?> ChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync("channel", cancellationToken, ("channelId", channelId));
        }
        catch (EclairRpcException)
        {
            return null;
        }
    }

    public async Task<JsonArray> ChannelsAsync(string nodeId, CancellationToken cancellationToken) =>
        (await CallAsync("channels", cancellationToken, ("nodeId", nodeId)))?.AsArray() ?? [];

    public async Task<JsonArray> ClosedChannelsAsync(string nodeId, CancellationToken cancellationToken) =>
        (await CallAsync("closedchannels", cancellationToken, ("nodeId", nodeId)))?.AsArray() ?? [];

    public async Task<JsonNode?> CloseAsync(string channelId, CancellationToken cancellationToken) =>
        await CallAsync("close", cancellationToken, ("channelId", channelId));

    /// <summary>
    /// <c>createinvoice</c>: the invoice (<c>serialized</c> is the BOLT 11 string, <c>paymentHash</c> the hash).
    /// </summary>
    public async Task<JsonNode> CreateInvoiceAsync(long amountMsat, string description,
                                                   CancellationToken cancellationToken) =>
        await CallAsync("createinvoice", cancellationToken, ("amountMsat", amountMsat),
                        ("description", description))
     ?? throw new EclairRpcException("createinvoice", 0, "empty answer");

    /// <summary>
    /// <c>payinvoice blocking=true</c>: the payment event (<c>type</c> <c>payment-sent</c> or <c>payment-failed</c>).
    /// </summary>
    public async Task<JsonNode> PayInvoiceAsync(string invoice, CancellationToken cancellationToken) =>
        await CallAsync("payinvoice", cancellationToken, ("invoice", invoice), ("blocking", true),
                        ("maxAttempts", 3))
     ?? throw new EclairRpcException("payinvoice", 0, "empty answer");

    public async Task<JsonNode?> GetReceivedInfoAsync(string paymentHash, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync("getreceivedinfo", cancellationToken, ("paymentHash", paymentHash));
        }
        catch (EclairRpcException)
        {
            return null;
        }
    }

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken) =>
        (await CallAsync("getnewaddress", cancellationToken))!.GetValue<string>();

    public async Task<JsonNode> OnchainBalanceAsync(CancellationToken cancellationToken) =>
        await CallAsync("onchainbalance", cancellationToken)
     ?? throw new EclairRpcException("onchainbalance", 0, "empty answer");

    /// <summary><c>splicein</c>: adds <paramref name="amountInSat"/> from Eclair's wallet to the channel.</summary>
    public async Task<JsonNode?> SpliceInAsync(string channelId, long amountInSat, CancellationToken cancellationToken) =>
        await CallAsync("splicein", cancellationToken, ("channelId", channelId), ("amountIn", amountInSat));

    /// <summary><c>spliceout</c>: pays <paramref name="amountOutSat"/> from the channel to <paramref name="address"/>.
    /// </summary>
    public async Task<JsonNode?> SpliceOutAsync(string channelId, long amountOutSat, string address,
                                                CancellationToken cancellationToken) =>
        await CallAsync("spliceout", cancellationToken, ("channelId", channelId), ("amountOut", amountOutSat),
                        ("address", address));

    /// <summary><c>rbfsplice</c>: RBF of Eclair's pending splice at <paramref name="feerateSatByte"/>.</summary>
    public async Task<JsonNode?> RbfSpliceAsync(string channelId, long feerateSatByte, long feeBudgetSat,
                                                CancellationToken cancellationToken) =>
        await CallAsync("rbfsplice", cancellationToken, ("channelId", channelId),
                        ("targetFeerateSatByte", feerateSatByte), ("fundingFeeBudgetSatoshis", feeBudgetSat));

    /// <summary><c>rbfopen</c>: RBF of Eclair's unconfirmed dual-funded open at <paramref name="feerateSatByte"/>.
    /// </summary>
    public async Task<JsonNode?> RbfOpenAsync(string channelId, long feerateSatByte, long feeBudgetSat,
                                              CancellationToken cancellationToken) =>
        await CallAsync("rbfopen", cancellationToken, ("channelId", channelId),
                        ("targetFeerateSatByte", feerateSatByte), ("fundingFeeBudgetSatoshis", feeBudgetSat));

    /// <summary><c>forceclose</c>: Eclair publishes its commitment.</summary>
    public async Task<JsonNode?> ForceCloseAsync(string channelId, CancellationToken cancellationToken) =>
        await CallAsync("forceclose", cancellationToken, ("channelId", channelId));

    /// <summary><c>allchannels</c>: the public channels in Eclair's graph.</summary>
    public async Task<JsonArray> AllChannelsAsync(CancellationToken cancellationToken) =>
        (await CallAsync("allchannels", cancellationToken))?.AsArray() ?? [];

    /// <summary><c>allupdates</c> of <paramref name="nodeId"/> (or every node when null).</summary>
    public async Task<JsonArray> AllUpdatesAsync(string? nodeId, CancellationToken cancellationToken) =>
        (await CallAsync("allupdates", cancellationToken, ("nodeId", nodeId)))?.AsArray() ?? [];

    /// <summary><c>nodes</c>: the node announcements in Eclair's graph.</summary>
    public async Task<JsonArray> NodesAsync(CancellationToken cancellationToken) =>
        (await CallAsync("nodes", cancellationToken))?.AsArray() ?? [];

    /// <summary><c>createoffer</c>: a BOLT 12 offer of Eclair's (the <c>encoded</c> string is the offer).</summary>
    public async Task<JsonNode> CreateOfferAsync(string description, long? amountMsat,
                                                 CancellationToken cancellationToken) =>
        await CallAsync("createoffer", cancellationToken, ("description", description), ("amountMsat", amountMsat))
     ?? throw new EclairRpcException("createoffer", 0, "empty answer");

    /// <summary><c>payoffer blocking=true</c>: pays a BOLT 12 offer (the payment event).</summary>
    public async Task<JsonNode> PayOfferAsync(string offer, long amountMsat, CancellationToken cancellationToken,
                                              bool connectDirectly = false) =>
        await CallAsync("payoffer", cancellationToken, ("offer", offer), ("amountMsat", amountMsat),
                        ("blocking", true), ("maxAttempts", 3), ("connectDirectly", connectDirectly))
     ?? throw new EclairRpcException("payoffer", 0, "empty answer");

    public void Dispose() => _http.Dispose();
}

public sealed class EclairRpcException(string method, int statusCode, string message)
    : Exception($"eclair {method} failed ({statusCode}): {message}")
{
    public string Method { get; } = method;
    public int StatusCode { get; } = statusCode;
    public string EclairMessage { get; } = message;
}