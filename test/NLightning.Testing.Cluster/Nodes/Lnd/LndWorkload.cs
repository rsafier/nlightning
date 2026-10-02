using System.Globalization;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Kube;

/// <summary>
/// An LND node as a <see cref="NodeWorkload"/>: the <c>custom_lnd</c> image (built from <c>test/Docker/custom_lnd</c>,
/// LND 0.20.0-beta) with the flags LNUnit's <c>AddPolarLNDNode</c> passes, its <c>lnddir</c> on the PVC (wallet, channel
/// database, <c>tls.cert</c> and macaroons survive a restart; <c>--noseedbackup</c> unlocks the wallet again at start),
/// and a readiness probe that passes only when LND's own gRPC <c>GetInfo</c> answers with <c>synced_to_chain</c>.
/// </summary>
public static class LndWorkload
{
    /// <summary>LND's directory in the image (the entrypoint runs LND as the <c>lnd</c> user with this home).</summary>
    public const string LndDir = "/home/lnd/.lnd";

    /// <summary>LND's self-signed TLS certificate (PEM).</summary>
    public const string TlsCertPath = LndDir + "/tls.cert";

    /// <summary>The admin macaroon on regtest.</summary>
    public const string AdminMacaroonPath = LndDir + "/data/chain/bitcoin/regtest/admin.macaroon";

    public const int P2pPort = 9735;
    public const int GrpcPort = 10009;
    public const int RestPort = 8080;

    /// <summary>
    /// The readiness probe's command: <c>lncli getinfo</c> over LND's gRPC with the admin macaroon (the probe runs as
    /// root, hence <c>--lnddir</c>), ready when it answers and reports <c>synced_to_chain</c>.
    /// </summary>
    public static IReadOnlyList<string> ReadinessCommand { get; } =
    [
        "sh", "-c",
        $"lncli --lnddir={LndDir} --network=regtest --rpcserver=localhost:{GrpcPort} getinfo 2>/dev/null"
      + " | grep -q '\"synced_to_chain\": *true'"
    ];

    /// <summary>The workload of <paramref name="options"/>.</summary>
    public static NodeWorkload Build(LndNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workload = new NodeWorkload(options.Alias, NodeKind.Lnd, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(LndDir, options.DataSize, Storage: options.Storage),
            // LND needs a few seconds to open its wallet; readiness failures never restart the pod
            ReadinessProbe = Probes.Exec(ReadinessCommand, periodSeconds: 2, timeoutSeconds: 10, failureThreshold: 3,
                                         initialDelaySeconds: 3),
            TerminationGracePeriodSeconds = 15
        };
        if (options.StartupWait is { } wait)
            workload.InitContainers.Add(wait);
        foreach (var arg in BuildArgs(options))
            workload.Args.Add(arg);
        workload.Ports.Add(new WorkloadPort("p2p", P2pPort));
        workload.Ports.Add(new WorkloadPort("grpc", GrpcPort));
        workload.Ports.Add(new WorkloadPort("rest", RestPort));
        return workload;
    }

    /// <summary>
    /// LND's command line (the image's entrypoint runs it as the <c>lnd</c> user). The defaults are LNUnit's
    /// <c>AddPolarLNDNode</c> flags; the TLS certificate also names the pod's stable DNS names, so in-cluster clients can
    /// verify it by host name. Host clients pin the certificate instead (the pod IP changes on restart).
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(LndNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var alias = options.Alias;
        var bitcoind = options.BitcoindHost;
        var args = new List<string>
        {
            "lnd",
            "--maxpendingchannels=10",
            "--noseedbackup",
            "--trickledelay=5000",
            $"--alias={alias}",
            $"--tlsextradomain={alias}",
            $"--tlsextradomain={alias}-0.{alias}",
            $"--listen=0.0.0.0:{P2pPort}",
            $"--rpclisten=0.0.0.0:{GrpcPort}",
            $"--restlisten=0.0.0.0:{RestPort}",
            "--bitcoin.active",
            "--bitcoin.regtest",
            "--bitcoin.node=bitcoind",
            $"--bitcoind.rpchost={bitcoind}:{options.BitcoindRpcPort.ToString(CultureInfo.InvariantCulture)}",
            $"--bitcoind.rpcuser={options.BitcoindRpcUser}",
            $"--bitcoind.rpcpass={options.BitcoindRpcPassword}",
            $"--bitcoind.zmqpubrawblock=tcp://{bitcoind}:{options.ZmqRawBlockPort.ToString(CultureInfo.InvariantCulture)}",
            $"--bitcoind.zmqpubrawtx=tcp://{bitcoind}:{options.ZmqRawTxPort.ToString(CultureInfo.InvariantCulture)}",
            "--db.bolt.auto-compact",
            "--db.bolt.auto-compact-min-age=0",
            "--protocol.wumbo-channels",
            "--gossip.sub-batch-delay=1s",
            "--gossip.max-channel-update-burst=100",
            "--gossip.channel-update-interval=1s"
        };
        if (options.AcceptKeysend)
            args.Add("--accept-keysend");
        args.AddRange(options.ExtraArgs);
        return args;
    }
}