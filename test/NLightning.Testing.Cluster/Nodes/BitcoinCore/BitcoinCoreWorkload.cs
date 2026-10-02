using System.Globalization;

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
            Data = new DataVolume(options.DataPath, options.DataSize, options.StorageClassName),
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

    /// <summary>The bitcoin-cli command line of one RPC call in the node's container.</summary>
    public static IReadOnlyList<string> CliCommand(BitcoinCoreOptions options, string? wallet, string method,
                                                   IReadOnlyDictionary<string, object?>? namedArgs = null) =>
        BitcoinCli.BuildCommand("regtest", BitcoinCorePorts.Rpc, options.RpcUser, options.RpcPassword, wallet,
                                method, namedArgs);
}