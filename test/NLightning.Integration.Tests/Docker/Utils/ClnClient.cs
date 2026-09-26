using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// A Core Lightning JSON-RPC client that runs <c>lightning-cli --network=regtest -k &lt;method&gt; key=value…</c> in the
/// CLN container (<c>docker exec</c> through the Docker API), so the tests need no TLS/rune setup and no docker CLI.
/// </summary>
public sealed class ClnClient(DockerClient client, string containerName)
{
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(90);

    public string ContainerName { get; } = containerName;

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

        var exec = await client.Exec.ExecCreateContainerAsync(ContainerName, new ContainerExecCreateParameters
        {
            Cmd = cmd,
            AttachStdout = true,
            AttachStderr = true
        }, timeoutCts.Token);
        string stdout, stderr;
        using (var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, timeoutCts.Token))
            (stdout, stderr) = await stream.ReadOutputToEndAsync(timeoutCts.Token);

        var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, timeoutCts.Token);
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

        if (inspect.ExitCode == 0 && json is not null)
            return json;

        if (json?["code"] is { } code)
            throw new ClnRpcException(method, code.GetValue<long>(), json["message"]?.GetValue<string>() ?? stdout);

        throw new ClnRpcException(method, inspect.ExitCode, $"{stdout} {stderr}".Trim());
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
/// A CLN JSON-RPC error.
/// </summary>
public sealed class ClnRpcException(string method, long code, string message)
    : Exception($"CLN {method} failed ({code}): {message}")
{
    public string Method { get; } = method;
    public long Code { get; } = code;
    public string ClnMessage { get; } = message;
}