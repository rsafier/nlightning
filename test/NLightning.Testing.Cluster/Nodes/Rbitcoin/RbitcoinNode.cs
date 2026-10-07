using System.Globalization;
using System.Net;

namespace NLightning.Testing.Cluster.Nodes.Rbitcoin;

using BitcoinCore;
using Images;
using Kube;
using Run;

/// <summary>
/// How an rbitcoin node of a run is deployed (NL-1095): regtest, following a bitcoind of the run over P2P
/// (<c>--connect</c>; rbitcoin has no wallet, so the bitcoind mines and funds), with its TCP JSON-RPC on the Core port
/// and HTTP Basic auth from a Core-format cookie file, the only auth our RPC client speaks.
/// </summary>
public sealed record RbitcoinNodeOptions
{
    /// <summary>The node's alias (StatefulSet, Service and container name).</summary>
    public string Name { get; init; } = "rbitcoin";

    /// <summary>The image; <see cref="ImageVersions.Rbitcoin"/> by default.</summary>
    public ImageRef Image { get; init; } = ImageVersions.Rbitcoin;

    /// <summary>The peer it follows (<c>host:port</c>: a bitcoind alias of the run or a pod IP).</summary>
    public required string Connect { get; init; }

    /// <summary>The Basic-auth user written to the cookie file.</summary>
    public string RpcUser { get; init; } = "nltg";

    /// <summary>The Basic-auth password written to the cookie file.</summary>
    public string RpcPassword { get; init; } = "nltg";

    /// <summary><c>--min-relay-tx-fee</c> in BTC/kvB, or null for rbitcoin's default (0.1 sat/vB).</summary>
    public decimal? MinRelayTxFeeBtcPerKvB { get; init; }

    /// <summary>More <c>rbitcoin-node</c> arguments.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;
}

/// <summary>
/// An rbitcoin node in a run's namespace (<see cref="NodeKind.Other"/>; NL-1095, the rbitcoin contract test). The test
/// process reaches its RPC by pod IP (OrbStack routes the pod network to the host), pods by its alias.
/// </summary>
public sealed class RbitcoinNode
{
    /// <summary>The data directory (an <c>emptyDir</c>: the node is never restarted).</summary>
    public const string DataPath = "/data";

    /// <summary>The cookie file rbitcoin reads (<c>user:password</c>, no trailing newline).</summary>
    public const string CookiePath = DataPath + "/rpc.cookie";

    private RbitcoinNode(KubeNodeHandle handle, RbitcoinNodeOptions options, string host)
    {
        Handle = handle;
        Options = options;
        Host = host;
    }

    public KubeNodeHandle Handle { get; }

    public RbitcoinNodeOptions Options { get; }

    /// <summary>The host the test process uses (the pod IP).</summary>
    public string Host { get; }

    /// <summary>The RPC URL the test process uses.</summary>
    public string RpcUrl => $"http://{Host}:{BitcoinCorePorts.Rpc}";

    /// <summary>The RPC URL other pods of the run use.</summary>
    public string ClusterRpcUrl => $"http://{Options.Name}:{BitcoinCorePorts.Rpc}";

    public NetworkCredential Credentials => new(Options.RpcUser, Options.RpcPassword);

    /// <summary><c>rbitcoin-node</c>'s arguments.</summary>
    public static IReadOnlyList<string> BuildArgs(RbitcoinNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string>
        {
            "--network", "regtest", "--datadir", $"{DataPath}/node", "--no-seeds",
            "--rpc-listen", $"0.0.0.0:{BitcoinCorePorts.Rpc}", "--rpc-cookie-file", CookiePath,
            "--connect", options.Connect
        };
        if (options.MinRelayTxFeeBtcPerKvB is { } minRelay)
            args.AddRange(["--min-relay-tx-fee", minRelay.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(options.ExtraArgs);
        return args;
    }

    /// <summary>
    /// The workload: a shell writes the cookie file (no trailing newline, as rbitcoin requires), then runs the node;
    /// ready once <c>getblockcount</c> answers over TCP with Basic auth.
    /// </summary>
    public static NodeWorkload Workload(RbitcoinNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RpcUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RpcPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Connect);

        var node = string.Join(' ', BuildArgs(options).Select(BitcoinCoreWorkload.ShellQuote));
        var cookie = BitcoinCoreWorkload.ShellQuote($"{options.RpcUser}:{options.RpcPassword}");
        var workload = new NodeWorkload(options.Name, NodeKind.Other, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(DataPath, "1Gi", Storage: NodeStorage.Ephemeral),
            Command = ["sh", "-c", $"mkdir -p {DataPath}/node && printf '%s' {cookie} > {CookiePath} && "
                                 + $"exec rbitcoin-node {node}"],
            ReadinessProbe = Probes.Exec(ReadinessCommand(options), periodSeconds: 1, timeoutSeconds: 5,
                                         failureThreshold: 3),
            TerminationGracePeriodSeconds = 15
        };
        workload.Ports.Add(new WorkloadPort("rpc", BitcoinCorePorts.Rpc));
        return workload;
    }

    /// <summary>The readiness probe: a Basic-auth <c>getblockcount</c> with curl (in the image).</summary>
    public static IReadOnlyList<string> ReadinessCommand(RbitcoinNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            "curl", "-sf", "-m", "4", "-u", $"{options.RpcUser}:{options.RpcPassword}", "-H",
            "content-type: application/json", "--data",
            """{"jsonrpc":"1.0","id":"ready","method":"getblockcount","params":[]}""",
            $"http://127.0.0.1:{BitcoinCorePorts.Rpc}/"
        ];
    }

    /// <summary>Deploys the node in <paramref name="run"/>'s namespace and waits until its RPC answers.</summary>
    public static async Task<RbitcoinNode> DeployAsync(TestRun run, RbitcoinNodeOptions options, TimeSpan readyTimeout,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var handle = await run.DeployAsync(Workload(options), readyTimeout, cancellationToken).ConfigureAwait(false);
        var host = handle.PodIp ?? throw new InvalidOperationException($"{handle} has no pod IP");
        return new RbitcoinNode(handle, options, host);
    }
}