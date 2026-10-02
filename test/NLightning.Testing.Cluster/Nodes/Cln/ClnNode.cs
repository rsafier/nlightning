using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.Cln;

using Kube;

/// <summary>
/// The Core Lightning workload: <c>elementsproject/lightningd</c> as a StatefulSet with its data (hsm_secret, the
/// database) on a PVC at <see cref="DataPath"/>, so a restart or a kill keeps the node id and its channels. Ready when
/// <c>lightning-cli getinfo</c> answers and the node is not draining (see <see cref="StopCommand"/>); being at the
/// chain's tip is the topology's wait, not the probe's, so a new block never takes the node out of its Services.
/// </summary>
public static class ClnNode
{
    /// <summary>CLN's lightning directory in the official image (<c>LIGHTNINGD_DATA</c>).</summary>
    public const string DataPath = "/root/.lightning";

    public const string Network = "regtest";

    public const int P2PPort = 9735;

    /// <summary>The workload of the CLN node <paramref name="name"/>.</summary>
    public static NodeWorkload Workload(string name, ClnNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workload = new NodeWorkload(name, NodeKind.Cln, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(DataPath, options.DataSize),
            ReadinessProbe = Probes.Exec(ReadinessCommand, periodSeconds: 1, timeoutSeconds: 5, failureThreshold: 3),
            TerminationGracePeriodSeconds = 15,
            // The image's PID 1 is a bash script that does not pass SIGTERM on to lightningd: drain, then stop it over
            // RPC, so a graceful restart is a clean shutdown and not a kill at the end of the grace period
            CustomizePod = spec => spec.Containers[0].Lifecycle = new V1Lifecycle
            {
                PreStop = new V1LifecycleHandler { Exec = new V1ExecAction { Command = [.. StopCommand] } }
            }
        };
        workload.Env["LIGHTNINGD_NETWORK"] = Network;
        workload.Ports.Add(new WorkloadPort("p2p", P2PPort));
        foreach (var arg in BuildArgs(name, options))
            workload.Args.Add(arg);

        return workload;
    }

    /// <summary>
    /// The <c>lightningd</c> arguments (the image's entrypoint adds <c>--network</c> from <c>LIGHTNINGD_NETWORK</c>).
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(string name, ClnNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> args =
        [
            $"--bitcoin-rpcconnect={options.BitcoindHost}",
            $"--bitcoin-rpcport={options.BitcoindRpcPort}",
            $"--bitcoin-rpcuser={options.BitcoindRpcUser}",
            $"--bitcoin-rpcpassword={options.BitcoindRpcPassword}",
            $"--bind-addr=0.0.0.0:{P2PPort}",
            $"--alias={options.Alias ?? name}",
            $"--log-level={options.LogLevel}",
            "--developer",
            // CLN polls bitcoind every 30 s by default; the tests mine and expect CLN at the tip within a second
            "--dev-bitcoind-poll=1"
        ];
        if (options.EnforceFeeLimits)
            args.Add("--ignore-fee-limits=false");
        args.AddRange(options.ExtraArgs);
        return args;
    }

    /// <summary>The flag file that fails the readiness probe while the node stops (in the container, not on the PVC).</summary>
    public const string DrainFile = "/tmp/nltg-draining";

    /// <summary>
    /// How long the stop waits after failing the probe, for the Services to drop the pod (3 failed probes 1 s apart,
    /// then kube-proxy's update).
    /// </summary>
    public const int DrainSeconds = 5;

    /// <summary>
    /// The <c>preStop</c> command. It first fails the readiness probe and waits <see cref="DrainSeconds"/>, so that
    /// kube-proxy removes the pod from its ClusterIP Service before the process stops. Without the drain, a peer's
    /// redial can reach the Service between the pod's network going away and kube-proxy's update; conntrack then pins
    /// that SYN to the vanished pod IP, and the dial hangs for about two minutes (CLN does not time it out). Then it
    /// runs <c>lightning-cli stop</c> and waits until the process is gone.
    /// </summary>
    public static IReadOnlyList<string> StopCommand { get; } =
    [
        "sh", "-c",
        $"touch {DrainFile}; sleep {DrainSeconds}; lightning-cli --network={Network} stop >/dev/null 2>&1; "
      + $"while [ -S {DataPath}/{Network}/lightning-rpc ] && lightning-cli --network={Network} getinfo "
      + ">/dev/null 2>&1; do sleep 0.2; done; exit 0"
    ];

    /// <summary>The readiness check: not draining, and <c>getinfo</c> answers.</summary>
    public static IReadOnlyList<string> ReadinessCommand { get; } =
    [
        "sh", "-c", $"[ ! -f {DrainFile} ] && lightning-cli --network={Network} getinfo >/dev/null"
    ];
}