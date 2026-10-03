using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore;

using Kube;
using Rpc;

/// <summary>
/// The <see cref="NodeWorkload"/> of a regtest bitcoind: StatefulSet + headless Service + data PVC, RPC, P2P and the
/// ZMQ feeds published on the Service, and a readiness probe that passes only when RPC answers
/// <c>getblockchaininfo</c> (out of warmup, wallet loaded).
/// </summary>
public static class BitcoinCoreWorkload
{
    /// <summary>Builds the workload for <paramref name="options"/>.</summary>
    public static NodeWorkload Build(BitcoinCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RpcUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RpcPassword);

        var workload = new NodeWorkload(options.Name, NodeKind.BitcoinCore, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(options.DataPath, options.DataSize, options.StorageClassName, options.Storage),
            ReadinessProbe = Probes.Exec(ReadinessCommand(options), options.ReadinessPeriodSeconds,
                                         timeoutSeconds: 5, failureThreshold: 3),
            // bitcoind flushes its chainstate and wallet on SIGTERM
            TerminationGracePeriodSeconds = 30
        };
        foreach (var arg in BuildArgs(options))
            workload.Args.Add(arg);

        workload.Ports.Add(new WorkloadPort("rpc", BitcoinCorePorts.Rpc));
        workload.Ports.Add(new WorkloadPort("p2p", BitcoinCorePorts.P2p));
        workload.Ports.Add(new WorkloadPort("zmq-block", BitcoinCorePorts.ZmqRawBlock));
        workload.Ports.Add(new WorkloadPort("zmq-tx", BitcoinCorePorts.ZmqRawTx));
        workload.Ports.Add(new WorkloadPort("zmq-hashblock", BitcoinCorePorts.ZmqHashBlock));
        return workload;
    }

    /// <summary>
    /// bitcoind's arguments. The images' entrypoint runs bitcoind (as the <c>bitcoin</c> user) when the first argument
    /// is an option, so the container keeps it.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(BitcoinCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string>
        {
            "-regtest", "-server=1", "-printtoconsole",
            $"-datadir={options.DataPath}",
            $"-rpcuser={options.RpcUser}", $"-rpcpassword={options.RpcPassword}",
            "-rpcbind=0.0.0.0", "-rpcallowip=0.0.0.0/0", $"-rpcport={BitcoinCorePorts.Rpc}", "-rpcworkqueue=1024",
            $"-zmqpubrawblock=tcp://0.0.0.0:{BitcoinCorePorts.ZmqRawBlock}",
            $"-zmqpubrawtx=tcp://0.0.0.0:{BitcoinCorePorts.ZmqRawTx}",
            $"-zmqpubhashblock=tcp://0.0.0.0:{BitcoinCorePorts.ZmqHashBlock}",
            "-listen=1", $"-port={BitcoinCorePorts.P2p}", "-bind=0.0.0.0", "-dnsseed=0", "-discover=0"
        };
        if (options.TxIndex)
            args.Add("-txindex=1");
        if (options.FallbackFeeBtcPerKvB is { } fallbackFee)
            args.Add($"-fallbackfee={fallbackFee.ToString(CultureInfo.InvariantCulture)}");
        foreach (var node in options.AddNodes)
            args.Add($"-addnode={(node.Contains(':') ? node : $"{node}:{BitcoinCorePorts.P2p}")}");
        args.AddRange(options.ExtraArgs);
        return args;
    }

    /// <summary>The readiness probe: <c>bitcoin-cli getblockchaininfo</c> fails during warmup and before RPC is up.</summary>
    public static IReadOnlyList<string> ReadinessCommand(BitcoinCoreOptions options) =>
        [
            "bitcoin-cli", "-regtest", $"-rpcport={BitcoinCorePorts.Rpc}", $"-rpcuser={options.RpcUser}",
            $"-rpcpassword={options.RpcPassword}", "-rpcclienttimeout=4", "getblockchaininfo"
        ];

    /// <summary>The name of <see cref="StartupWaitContainer"/>.</summary>
    public const string StartupWaitContainerName = "wait-for-chain";

    /// <summary>How long <see cref="StartupWaitContainer"/> waits before it lets the node start anyway.</summary>
    public const int DefaultStartupWaitSeconds = 180;

    /// <summary>
    /// An init container (bitcoind's own image, so nothing new is pulled) that polls <c>getblockchaininfo</c> on
    /// <paramref name="options"/>' node from another pod every 0.2 s until it answers (resolved, RPC up and out of
    /// warmup), and after <paramref name="timeoutSeconds"/> exits 0 anyway, so a node restarted while bitcoind is
    /// partitioned or paused still starts as it did without the wait.
    /// </summary>
    public static V1Container StartupWaitContainer(BitcoinCoreOptions options,
                                                   int timeoutSeconds = DefaultStartupWaitSeconds)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);

        var cli = string.Join(' ', new[]
        {
            "bitcoin-cli", "-regtest", $"-rpcconnect={options.Name}", $"-rpcport={BitcoinCorePorts.Rpc}",
            $"-rpcuser={options.RpcUser}", $"-rpcpassword={options.RpcPassword}", "-rpcclienttimeout=2",
            "getblockchaininfo"
        }.Select(ShellQuote));
        var target = $"{options.Name}:{BitcoinCorePorts.Rpc}";
        var timeout = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        // Logs how long it waited, how many calls and the last error (the pod's init log, for diagnostics)
        var script =
            $"start=$(date +%s); end=$((start + {timeout})); n=0; "
          + $"until err=$({cli} 2>&1 >/dev/null); do n=$((n + 1)); last=$err; "
          + $"if [ \"$(date +%s)\" -ge \"$end\" ]; then echo \"wait-for-chain: {target} not answering after "
          + $"{timeout} s ($n calls, last: $last), starting anyway\"; exit 0; fi; sleep 0.2; done; "
          + $"echo \"wait-for-chain: {target} answers after $(($(date +%s) - start)) s, $n failed call(s)"
          + "${last:+, last: $last}\"";
        return new V1Container
        {
            Name = StartupWaitContainerName,
            Image = options.Image.Reference,
            ImagePullPolicy = options.Image.PullPolicyValue,
            Command = ["sh", "-c", script],
            Resources = WorkloadResources.Tiny.ToKubernetes()
        };
    }

    /// <summary>Single-quotes <paramref name="value"/> for <c>sh</c>.</summary>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The bitcoin-cli command line of one RPC call in the node's container.</summary>
    public static IReadOnlyList<string> CliCommand(BitcoinCoreOptions options, string? wallet, string method,
                                                   IReadOnlyDictionary<string, object?>? namedArgs = null) =>
        BitcoinCli.BuildCommand("regtest", BitcoinCorePorts.Rpc, options.RpcUser, options.RpcPassword, wallet,
                                method, namedArgs);
}