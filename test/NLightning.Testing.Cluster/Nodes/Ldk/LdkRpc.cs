using System.Text.Json;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Ldk;

using Kube;

/// <summary>
/// ldk-server's API through <c>ldk-server-cli -c /data/config.toml &lt;subcommand&gt; args…</c> run in the node's pod
/// (the approach of the Docker suite's <c>LdkClient</c>, over a Kubernetes exec instead of <c>docker exec</c>): no
/// gRPC, TLS or API key setup of the caller's own. The CLI prints the gRPC response as JSON (snake_case proto fields).
/// </summary>
public sealed class LdkRpc
{
    /// <summary>How long one call may take (a <c>pay --wait</c> included).</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);

    private readonly TimeSpan _callTimeout;

    public LdkRpc(INodeHandle node, TimeSpan? callTimeout = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        _callTimeout = callTimeout ?? DefaultCallTimeout;
    }

    /// <summary>The LDK node the calls run in.</summary>
    public INodeHandle Node { get; }

    /// <summary>Runs <paramref name="subcommand"/> with <paramref name="args"/>.</summary>
    /// <returns>The JSON the CLI printed.</returns>
    /// <exception cref="LdkRpcException">The CLI exited non-zero or printed no JSON.</exception>
    public async Task<JsonNode> RunAsync(string subcommand, CancellationToken cancellationToken,
                                         params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_callTimeout);
        var result = await Node.ExecAsync(LdkNode.CliCommand(subcommand, args), timeout.Token).ConfigureAwait(false);
        return ParseResult(subcommand, result);
    }

    public Task<JsonNode> GetNodeInfoAsync(CancellationToken cancellationToken) =>
        RunAsync("get-node-info", cancellationToken);

    /// <summary>
    /// The JSON result of a finished call, or the <see cref="LdkRpcException"/> it stands for (with the JSON the CLI
    /// printed before failing, e.g. a failed payment's details).
    /// </summary>
    public static JsonNode ParseResult(string subcommand, ExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var stdout = result.StdOutText;
        JsonNode? json = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stdout))
                json = JsonNode.Parse(stdout);
        }
        catch (JsonException)
        {
            // not JSON: reported below
        }

        if (result.Succeeded && json is not null)
            return json;

        throw new LdkRpcException(subcommand, result.ExitCode, $"{result.StdErrText} {stdout}".Trim(), json);
    }
}

/// <summary>An <c>ldk-server-cli</c> call that exited non-zero (or printed no JSON).</summary>
public sealed class LdkRpcException(string subcommand, long exitCode, string message, JsonNode? json)
    : Exception($"ldk-server-cli {subcommand} failed ({exitCode}): {message}")
{
    public string Subcommand { get; } = subcommand;

    public long ExitCode { get; } = exitCode;

    public string LdkMessage { get; } = message;

    /// <summary>The JSON the CLI printed before failing, if any.</summary>
    public JsonNode? Json { get; } = json;
}