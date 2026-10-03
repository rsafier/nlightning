using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// A Core Lightning JSON-RPC client that runs <c>lightning-cli --network=regtest -k &lt;method&gt; key=value…</c> in the
/// CLN node, so the tests need no TLS/rune setup. Where the command runs is the caller's <see cref="ClnExec"/>: a
/// Kubernetes exec in the node's pod (<c>Fixtures/Cln/ClusterClnBackend</c>) or, for the Tor suite's container, a
/// <c>docker exec</c> (<c>Fixtures/Tor/TorInteropFixture</c>).
/// </summary>
public sealed class ClnClient
{
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(90);

    private readonly ClnExec _exec;

    /// <summary>A client that runs its commands through <paramref name="exec"/> in the node <paramref name="name"/>.</summary>
    public ClnClient(string name, ClnExec exec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ContainerName = name;
        _exec = exec ?? throw new ArgumentNullException(nameof(exec));
    }

    /// <summary>The node (or the Tor suite's container) the commands run in.</summary>
    public string ContainerName { get; }

    /// <summary>
    /// Runs <paramref name="command"/> in the CLN node and returns its exit code and output, without interpreting
    /// them (a call whose error data matters, e.g. an <c>invoice_error</c>).
    /// </summary>
    public Task<ClnExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken) =>
        _exec(command, cancellationToken);

    /// <summary>
    /// Calls <paramref name="method"/> with named parameters (values are passed as <c>key=value</c>; a JSON value
    /// such as an array is passed verbatim).
    /// </summary>
    /// <returns>The JSON result.</returns>
    /// <exception cref="ClnRpcException">CLN returned an error object (its code and message).</exception>
    public async Task<JsonNode> CallAsync(string method, CancellationToken cancellationToken,
                                          params (string Key, object Value)[] parameters)
    {
        List<string> cmd = ["lightning-cli", "--network=regtest", "--notifications=none", "-k", method];
        cmd.AddRange(parameters.Select(p => $"{p.Key}={Format(p.Value)}"));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_callTimeout);

        var (exitCode, stdout, stderr) = await _exec(cmd, timeoutCts.Token);
        JsonNode? json = null;
        try
        {
            // Notification lines (e.g. xpay's progress) start with '#'; the JSON result follows them
            var body = string.Join('\n', stdout.Split('\n').Where(l => !l.StartsWith('#')));
            if (!string.IsNullOrWhiteSpace(body))
                json = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON: reported below
        }

        if (exitCode == 0 && json is not null)
            return json;

        if (json?["code"] is { } code)
            throw new ClnRpcException(method, code.GetValue<long>(), json["message"]?.GetValue<string>() ?? stdout);

        throw new ClnRpcException(method, exitCode, $"{stdout} {stderr}".Trim());
    }

    public Task<JsonNode> GetInfoAsync(CancellationToken cancellationToken) =>
        CallAsync("getinfo", cancellationToken);

    /// <summary>
    /// <c>listpeerchannels</c> for <paramref name="peerIdHex"/>.
    /// </summary>
    public async Task<JsonArray> ListPeerChannelsAsync(string peerIdHex, CancellationToken cancellationToken)
    {
        var result = await CallAsync("listpeerchannels", cancellationToken, ("id", peerIdHex));
        return result["channels"]!.AsArray();
    }

    /// <summary>
    /// The channel with our channel id (<c>channel_id</c>, hex), or <c>null</c>.
    /// </summary>
    public async Task<JsonNode?> GetPeerChannelAsync(string peerIdHex, string channelIdHex,
                                                     CancellationToken cancellationToken)
    {
        var channels = await ListPeerChannelsAsync(peerIdHex, cancellationToken);
        return channels.FirstOrDefault(c => string.Equals(c?["channel_id"]?.GetValue<string>(), channelIdHex,
                                                          StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>listpeers</c> entry for <paramref name="peerIdHex"/>, or <c>null</c>.
    /// </summary>
    public async Task<JsonNode?> GetPeerAsync(string peerIdHex, CancellationToken cancellationToken)
    {
        var result = await CallAsync("listpeers", cancellationToken, ("id", peerIdHex));
        return result["peers"]!.AsArray().FirstOrDefault();
    }

    public async Task<bool> IsConnectedAsync(string peerIdHex, CancellationToken cancellationToken) =>
        (await GetPeerAsync(peerIdHex, cancellationToken))?["connected"]?.GetValue<bool>() == true;

    /// <summary>
    /// The last lines of CLN's log at <paramref name="level"/> or above that mention <paramref name="fragment"/> (for
    /// failure messages).
    /// </summary>
    public async Task<string> GetLogLinesAsync(string fragment, CancellationToken cancellationToken, int max = 40,
                                               string level = "debug")
    {
        try
        {
            var result = await CallAsync("getlog", cancellationToken, ("level", level));
            var lines = result["log"]!.AsArray()
                                      .Where(e => e?["type"]?.GetValue<string>() != "SKIPPED")
                                      .Select(e => $"{e?["time"]} {e?["type"]} {e?["source"]}: {e?["log"]}")
                                      .Where(l => l.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                                      .TakeLast(max);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception e)
        {
            return $"(getlog failed: {e.Message})";
        }
    }

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        JsonNode node => node.ToJsonString(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}

/// <summary>
/// Runs a command (<c>lightning-cli ...</c>) in a CLN node.
/// </summary>
public delegate Task<ClnExecResult> ClnExec(IReadOnlyList<string> command, CancellationToken cancellationToken);

/// <summary>
/// What a command run in a CLN node returned.
/// </summary>
public sealed record ClnExecResult(long ExitCode, string StdOut, string StdErr);

/// <summary>
/// A CLN JSON-RPC error.
/// </summary>
public sealed class ClnRpcException(string method, long code, string message)
    : Exception($"CLN {method} failed ({code}): {message}")
{
    public string Method { get; } = method;
    public long Code { get; } = code;
    public string ClnMessage { get; } = message;
}