using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// An ldk-server client that runs <c>ldk-server-cli -c /data/config.toml &lt;subcommand&gt; args…</c> in the LDK node,
/// so the tests need no gRPC, TLS or macaroon setup of their own (NL-180). The CLI prints the gRPC response as JSON
/// (snake_case proto fields). The command runs through the caller's <see cref="LdkExec"/>: a Kubernetes exec in the
/// node's pod (<c>Fixtures/Ldk/ClusterLdkBackend</c>).
/// </summary>
public sealed class LdkClient
{
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(120);

    private readonly string _configPath;
    private readonly LdkExec _exec;

    /// <summary>
    /// A client that runs its commands through <paramref name="exec"/> in the node <paramref name="name"/>, with the
    /// configuration at <paramref name="configPath"/> there.
    /// </summary>
    public LdkClient(string name, string configPath, LdkExec exec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ContainerName = name;
        _configPath = configPath;
        _exec = exec ?? throw new ArgumentNullException(nameof(exec));
    }

    /// <summary>The node the commands run in.</summary>
    public string ContainerName { get; }

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

    /// <summary>
    /// <c>open-channel ... --announce-channel</c> (NL-556): a public channel, which LDK Node opens only when it has an
    /// alias and listening addresses (<see cref="Fixtures.LdkFixture"/> configures both).
    /// </summary>
    /// <returns>LDK's <c>user_channel_id</c>.</returns>
    public async Task<string> OpenAnnouncedChannelAsync(string nodeId, string hostPort, long channelSat,
                                                        CancellationToken cancellationToken)
    {
        var result = await RunAsync("open-channel", cancellationToken, nodeId, hostPort, $"{channelSat}sat",
                                    "--announce-channel");
        return result["user_channel_id"]!.GetValue<string>();
    }

    /// <summary><c>force-close-channel &lt;user_channel_id&gt; &lt;peer&gt;</c>: LDK broadcasts its commitment.</summary>
    public Task<JsonNode> ForceCloseChannelAsync(string userChannelId, string peerNodeId,
                                                 CancellationToken cancellationToken) =>
        RunAsync("force-close-channel", cancellationToken, userChannelId, peerNodeId, "--force-close-reason",
                 "nltg interop proof");

    /// <summary>
    /// <c>bolt11-receive-for-hash &lt;hash&gt; &lt;sat&gt;</c>: a hold invoice; LDK keeps the HTLC until
    /// <see cref="Bolt11ClaimForIdAsync"/> or <see cref="Bolt11FailForIdAsync"/>.
    /// </summary>
    /// <returns>The invoice.</returns>
    public async Task<string> Bolt11ReceiveForHashAsync(string paymentHashHex, long amountSat, string description,
                                                        CancellationToken cancellationToken) =>
        (await RunAsync("bolt11-receive-for-hash", cancellationToken, paymentHashHex, $"{amountSat}sat", "-d",
                        description))["invoice"]!.GetValue<string>();

    /// <summary>
    /// <c>bolt11-claim-for-id &lt;payment_id&gt; &lt;preimage&gt;</c>: LDK Node's <c>payment_id</c> of the held payment
    /// (<c>list-payments</c>; for a hold invoice it is not the payment hash).
    /// </summary>
    public Task<JsonNode> Bolt11ClaimForIdAsync(string paymentIdHex, string preimageHex,
                                                CancellationToken cancellationToken) =>
        RunAsync("bolt11-claim-for-id", cancellationToken, paymentIdHex, preimageHex);

    /// <summary><c>bolt11-fail-for-id &lt;payment_id&gt;</c>: fails a held payment back.</summary>
    public Task<JsonNode> Bolt11FailForIdAsync(string paymentIdHex, CancellationToken cancellationToken) =>
        RunAsync("bolt11-fail-for-id", cancellationToken, paymentIdHex);

    /// <summary>
    /// <c>bolt12-receive &lt;description&gt; [amount]</c>: a BOLT 12 offer (amountless without
    /// <paramref name="amountSat"/>).
    /// </summary>
    /// <returns><c>offer</c> and <c>offer_id</c>.</returns>
    public Task<JsonNode> Bolt12ReceiveAsync(string description, long? amountSat,
                                             CancellationToken cancellationToken) =>
        amountSat is { } amount
            ? RunAsync("bolt12-receive", cancellationToken, description, $"{amount}sat")
            : RunAsync("bolt12-receive", cancellationToken, description);

    /// <summary>
    /// <c>pay &lt;offer&gt; [amount] --wait</c>: LDK fetches our invoice over onion messages and pays it; the payment's
    /// details once it succeeded (a failure or a timeout throws <see cref="LdkCliException"/>).
    /// </summary>
    public Task<JsonNode> PayOfferAsync(string offer, long? amountMsat, int timeoutSeconds,
                                        CancellationToken cancellationToken)
    {
        List<string> args = [offer];
        if (amountMsat is { } amount)
            args.Add($"{amount}msat");
        args.AddRange(["--wait", "--wait-timeout",
                       timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return RunAsync("pay", cancellationToken, [.. args]);
    }

    /// <summary>
    /// <c>spontaneous-send &lt;node&gt; &lt;msat&gt; [--custom-tlv type:hex]...</c> (keysend).
    /// </summary>
    /// <returns>LDK's <c>payment_id</c>.</returns>
    public async Task<string> SpontaneousSendAsync(string nodeId, long amountMsat, CancellationToken cancellationToken,
                                                   params string[] customTlvs)
    {
        List<string> args = [nodeId, $"{amountMsat}msat"];
        foreach (var tlv in customTlvs)
            args.AddRange(["--custom-tlv", tlv]);
        return (await RunAsync("spontaneous-send", cancellationToken, [.. args]))["payment_id"]!.GetValue<string>();
    }

    /// <summary>The payment's <c>status</c> (<c>PENDING</c>, <c>SUCCEEDED</c>, <c>FAILED</c>), or null.</summary>
    public static string? StatusOf(JsonNode? payment) => payment?["status"]?.ToString();

    /// <summary><c>graph-get-channel &lt;scid&gt;</c>: the channel in LDK's network graph, or null.</summary>
    public async Task<JsonNode?> GraphGetChannelAsync(ulong shortChannelId, CancellationToken cancellationToken)
    {
        try
        {
            return (await RunAsync("graph-get-channel", cancellationToken,
                                   shortChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                ["channel"];
        }
        catch (LdkCliException)
        {
            return null;
        }
    }

    /// <summary><c>graph-get-node &lt;id&gt;</c>: the node in LDK's network graph, or null.</summary>
    public async Task<JsonNode?> GraphGetNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        try
        {
            return (await RunAsync("graph-get-node", cancellationToken, nodeId))["node"];
        }
        catch (LdkCliException)
        {
            return null;
        }
    }

    private async Task<LdkExecResult> ExecAsync(string subcommand, IEnumerable<string> args,
                                                CancellationToken cancellationToken)
    {
        List<string> cmd = ["ldk-server-cli", "-c", _configPath, subcommand];
        cmd.AddRange(args);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_callTimeout);
        return await _exec(cmd, timeoutCts.Token);
    }
}

/// <summary>
/// Runs a command (<c>ldk-server-cli ...</c>) in an LDK node.
/// </summary>
public delegate Task<LdkExecResult> LdkExec(IReadOnlyList<string> command, CancellationToken cancellationToken);

/// <summary>
/// What a command run in an LDK node returned.
/// </summary>
public sealed record LdkExecResult(long ExitCode, string StdOut, string StdErr);

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