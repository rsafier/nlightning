using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// An ldk-server client that runs <c>ldk-server-cli -c /data/config.toml &lt;subcommand&gt; args…</c> in the LDK
/// container (<c>docker exec</c> through the Docker API, as <see cref="ClnClient"/> does), so the tests need no gRPC,
/// TLS or macaroon setup of their own (NL-180). The CLI prints the gRPC response as JSON (snake_case proto fields).
/// </summary>
public sealed class LdkClient(DockerClient client, string containerName, string configPath)
{
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(120);

    public string ContainerName { get; } = containerName;

    /// <summary>
    /// Runs <paramref name="subcommand"/> with <paramref name="args"/>.
    /// </summary>
    /// <returns>The JSON the CLI printed.</returns>
    /// <exception cref="LdkCliException">The CLI exited non-zero (its stderr, e.g. <c>Error: ...</c>).</exception>
    public async Task<JsonNode> RunAsync(string subcommand, CancellationToken cancellationToken, params string[] args)
    {
        var (exitCode, stdout, stderr) = await ExecAsync(subcommand, args, cancellationToken);
        JsonNode? json = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stdout))
                json = JsonNode.Parse(stdout);
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON: reported below
        }

        if (exitCode == 0 && json is not null)
            return json;

        throw new LdkCliException(subcommand, exitCode, $"{stderr} {stdout}".Trim(), json);
    }

    public Task<JsonNode> GetNodeInfoAsync(CancellationToken cancellationToken) =>
        RunAsync("get-node-info", cancellationToken);

    public async Task<uint> GetBlockHeightAsync(CancellationToken cancellationToken) =>
        (await GetNodeInfoAsync(cancellationToken))["current_best_block"]?["height"]?.GetValue<uint>() ?? 0;

    public Task<JsonNode> GetBalancesAsync(CancellationToken cancellationToken) =>
        RunAsync("get-balances", cancellationToken);

    /// <summary>A new on-chain address of LDK's wallet.</summary>
    public async Task<string> OnchainReceiveAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("onchain-receive", cancellationToken);
        return result["address"]!.GetValue<string>();
    }

    /// <summary><c>connect-peer id@host:port --persist</c>.</summary>
    public Task<JsonNode> ConnectPeerAsync(string nodeId, string hostPort, CancellationToken cancellationToken) =>
        RunAsync("connect-peer", cancellationToken, $"{nodeId}@{hostPort}", "--persist");

    public async Task<JsonArray> ListPeersAsync(CancellationToken cancellationToken) =>
        (await RunAsync("list-peers", cancellationToken))["peers"]?.AsArray() ?? [];

    public async Task<bool> IsConnectedAsync(string nodeId, CancellationToken cancellationToken) =>
        (await ListPeersAsync(cancellationToken)).Any(p => string.Equals(p?["node_id"]?.GetValue<string>(), nodeId,
                                                                         StringComparison.OrdinalIgnoreCase)
                                                        && p?["is_connected"]?.GetValue<bool>() == true);

    /// <summary>
    /// <c>open-channel</c> of <paramref name="channelSat"/> (private) to <paramref name="nodeId"/> at
    /// <paramref name="hostPort"/>, <paramref name="pushSat"/> pushed.
    /// </summary>
    /// <returns>LDK's <c>user_channel_id</c>.</returns>
    public async Task<string> OpenChannelAsync(string nodeId, string hostPort, long channelSat, long? pushSat,
                                               CancellationToken cancellationToken)
    {
        List<string> args = [nodeId, hostPort, $"{channelSat}sat"];
        if (pushSat is not null)
        {
            args.Add("--push-to-counterparty");
            args.Add($"{pushSat}sat");
        }

        var result = await RunAsync("open-channel", cancellationToken, [.. args]);
        return result["user_channel_id"]!.GetValue<string>();
    }

    public async Task<JsonArray> ListChannelsAsync(CancellationToken cancellationToken) =>
        (await RunAsync("list-channels", cancellationToken))["channels"]?.AsArray() ?? [];

    /// <summary>The channel with <paramref name="channelIdHex"/> (hex <c>channel_id</c>), or <c>null</c>.</summary>
    public async Task<JsonNode?> GetChannelAsync(string channelIdHex, CancellationToken cancellationToken) =>
        (await ListChannelsAsync(cancellationToken))
       .FirstOrDefault(c => string.Equals(c?["channel_id"]?.GetValue<string>(), channelIdHex,
                                          StringComparison.OrdinalIgnoreCase));

    /// <summary><c>close-channel &lt;user_channel_id&gt; &lt;peer&gt;</c> (cooperative).</summary>
    public Task<JsonNode> CloseChannelAsync(string userChannelId, string peerNodeId,
                                            CancellationToken cancellationToken) =>
        RunAsync("close-channel", cancellationToken, userChannelId, peerNodeId);

    /// <summary>
    /// <c>splice-in &lt;user_channel_id&gt; &lt;peer&gt; &lt;sat&gt;</c> from LDK's wallet (NL-556). LDK Node answers once
    /// it handed its contribution to the channel manager; the negotiation (<c>stfu</c>, <c>splice_init</c>, ...) runs
    /// after that, at LDK's <c>ChannelFunding</c> feerate estimate.
    /// </summary>
    public Task<JsonNode> SpliceInAsync(string userChannelId, string peerNodeId, long amountSat,
                                        CancellationToken cancellationToken) =>
        RunAsync("splice-in", cancellationToken, userChannelId, peerNodeId, $"{amountSat}sat");

    /// <summary>
    /// <c>splice-out &lt;user_channel_id&gt; &lt;peer&gt; &lt;sat&gt; --address &lt;address&gt;</c> (NL-556), answered as
    /// <see cref="SpliceInAsync"/>.
    /// </summary>
    public Task<JsonNode> SpliceOutAsync(string userChannelId, string peerNodeId, long amountSat, string address,
                                         CancellationToken cancellationToken) =>
        RunAsync("splice-out", cancellationToken, userChannelId, peerNodeId, $"{amountSat}sat", "--address", address);

    /// <summary><c>bump-channel-funding-fee &lt;user_channel_id&gt; &lt;peer&gt;</c>: RBF of LDK's pending splice.</summary>
    public Task<JsonNode> BumpChannelFundingFeeAsync(string userChannelId, string peerNodeId,
                                                     CancellationToken cancellationToken) =>
        RunAsync("bump-channel-funding-fee", cancellationToken, userChannelId, peerNodeId);

    /// <summary><c>bolt11-receive</c> of <paramref name="amountSat"/>.</summary>
    /// <returns><c>invoice</c>, <c>payment_hash</c>, <c>payment_secret</c>.</returns>
    public Task<JsonNode> Bolt11ReceiveAsync(long amountSat, string description, CancellationToken cancellationToken) =>
        RunAsync("bolt11-receive", cancellationToken, $"{amountSat}sat", "-d", description);

    /// <summary>
    /// <c>pay --wait --wait-timeout N</c>: the payment's details once it succeeded; a failure or a timeout throws
    /// <see cref="LdkCliException"/> (exit 2 or 3, with the payment's JSON when the CLI printed it).
    /// </summary>
    public Task<JsonNode> PayAsync(string invoice, int timeoutSeconds, CancellationToken cancellationToken) =>
        RunAsync("pay", cancellationToken, invoice, "--wait", "--wait-timeout",
                 timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary><c>get-payment-details</c>: for BOLT 11 the payment id is the payment hash.</summary>
    public async Task<JsonNode?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken)
    {
        try
        {
            return (await RunAsync("get-payment-details", cancellationToken, paymentId))["payment"];
        }
        catch (LdkCliException)
        {
            return null;
        }
    }

    /// <summary>
    /// The payment whose BOLT 11 <c>hash</c> is <paramref name="paymentHash"/>, from <c>get-payment-details</c> or else
    /// <c>list-payments</c>, or <c>null</c>.
    /// </summary>
    public async Task<JsonNode?> FindPaymentByHashAsync(string paymentHash, CancellationToken cancellationToken)
    {
        if (await GetPaymentAsync(paymentHash, cancellationToken) is { } direct)
            return direct;

        var payments = await RunAsync("list-payments", cancellationToken, "-n", "1000");
        return (payments["list"]?.AsArray() ?? [])
           .FirstOrDefault(p => string.Equals(p?["kind"]?["kind"]?["bolt11"]?["hash"]?.GetValue<string>(), paymentHash,
                                              StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p?["payment_id"]?.GetValue<string>(), paymentHash,
                                              StringComparison.OrdinalIgnoreCase));
    }

    public async Task<JsonArray> ListPaymentsAsync(CancellationToken cancellationToken) =>
        (await RunAsync("list-payments", cancellationToken))["list"]?.AsArray() ?? [];

    private async Task<(long ExitCode, string Stdout, string Stderr)> ExecAsync(
        string subcommand, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        List<string> cmd = ["ldk-server-cli", "-c", configPath, subcommand];
        cmd.AddRange(args);

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
        return (inspect.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// An <c>ldk-server-cli</c> call that exited non-zero.
/// </summary>
public sealed class LdkCliException(string subcommand, long exitCode, string message, JsonNode? json)
    : Exception($"ldk-server-cli {subcommand} failed ({exitCode}): {message}")
{
    public string Subcommand { get; } = subcommand;
    public long ExitCode { get; } = exitCode;
    public string LdkMessage { get; } = message;

    /// <summary>The JSON the CLI printed before failing (e.g. a failed payment's details), if any.</summary>
    public JsonNode? Json { get; } = json;
}