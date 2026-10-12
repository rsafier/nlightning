using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Cln;

using Kube;

/// <summary>
/// CLN's JSON-RPC through <c>lightning-cli --network=regtest -k &lt;method&gt; key=value…</c> run in the node's pod
/// (the approach of the Docker suite's <c>ClnClient</c>, over a Kubernetes exec instead of <c>docker exec</c>): no
/// rune, TLS or port to expose, and it works the same from the host and from an in-cluster runner.
/// </summary>
public sealed class ClnRpc
{
    /// <summary>How long one call may take (a payment with <c>retry_for</c> included).</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(90);

    private readonly TimeSpan _callTimeout;

    public ClnRpc(INodeHandle node, TimeSpan? callTimeout = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        _callTimeout = callTimeout ?? DefaultCallTimeout;
    }

    /// <summary>The CLN node the calls run in.</summary>
    public INodeHandle Node { get; }

    /// <summary>
    /// Calls <paramref name="method"/> with named parameters (<c>key=value</c>; a <see cref="JsonNode"/> value, such
    /// as an array, is passed as JSON).
    /// </summary>
    /// <returns>The JSON result.</returns>
    /// <exception cref="ClnRpcException">CLN returned an error object, or the call failed without one.</exception>
    public async Task<JsonNode> CallAsync(string method, CancellationToken cancellationToken,
                                          params (string Key, object Value)[] parameters)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_callTimeout);
        var result = await Node.ExecAsync(BuildCommand(method, parameters), timeout.Token).ConfigureAwait(false);
        return ParseResult(method, result);
    }

    public Task<JsonNode> GetInfoAsync(CancellationToken cancellationToken) =>
        CallAsync("getinfo", cancellationToken);

    /// <summary>The <c>lightning-cli</c> command line of a call.</summary>
    public static IReadOnlyList<string> BuildCommand(string method, IEnumerable<(string Key, object Value)> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        List<string> command =
            ["lightning-cli", $"--network={ClnNode.Network}", "--notifications=none", "-k", method];
        command.AddRange(parameters.Select(p => $"{p.Key}={Format(p.Value)}"));
        return command;
    }

    /// <summary>
    /// The JSON result of a finished call, or the <see cref="ClnRpcException"/> it stands for. Notification lines
    /// (<c>#</c>, e.g. xpay's progress) before the JSON are skipped.
    /// </summary>
    public static JsonNode ParseResult(string method, ExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var stdout = result.StdOutText;
        JsonNode? json = null;
        try
        {
            var body = string.Join('\n', stdout.Split('\n').Where(l => !l.StartsWith('#')));
            if (!string.IsNullOrWhiteSpace(body))
                json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // not JSON: reported below
        }

        if (result.Succeeded && json is not null)
            return json;

        if (json?["code"] is { } code)
            throw new ClnRpcException(method, code.GetValue<long>(), json["message"]?.GetValue<string>() ?? stdout);

        throw new ClnRpcException(method, result.ExitCode, $"{stdout} {result.StdErrText}".Trim());
    }

    internal static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        JsonNode node => node.ToJsonString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}

/// <summary>A CLN JSON-RPC error (its code and message), or a failed <c>lightning-cli</c> run (its exit code).</summary>
public sealed class ClnRpcException(string method, long code, string message)
    : Exception($"CLN {method} failed ({code}): {message}")
{
    public string Method { get; } = method;

    public long Code { get; } = code;

    public string ClnMessage { get; } = message;
}