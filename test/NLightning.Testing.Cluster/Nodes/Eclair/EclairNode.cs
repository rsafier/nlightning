using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.Eclair;

using Kube;

/// <summary>
/// The Eclair workload: the locally built <c>nltg-eclair</c> image as a StatefulSet with its data directory on a PVC
/// at <see cref="DataPath"/> (a restart keeps the node id and its channels). The container writes
/// <see cref="BuildConfig"/> (passed in <see cref="ConfigVariable"/>) to <c>eclair.conf</c> and runs the image's start
/// script, as the Docker fixture's container does with the file it copies in. An init container creates or loads the
/// bitcoind wallet Eclair funds from and waits until bitcoind left its initial block download. Ready when the JSON API
/// answers <c>getinfo</c> and the node is not draining (<see cref="StopCommand"/>); being at the chain's tip is the
/// topology's wait.
/// </summary>
public static class EclairNode
{
    /// <summary>Eclair's data directory (<c>ECLAIR_DATADIR</c> in the image).</summary>
    public const string DataPath = "/data";

    public const int P2PPort = 9735;

    /// <summary>The JSON API port.</summary>
    public const int ApiPort = 8080;

    /// <summary>The environment variable that carries <c>eclair.conf</c> into the container.</summary>
    public const string ConfigVariable = "NLTG_ECLAIR_CONF";

    /// <summary>The environment variable that carries the API password (the probe and the diagnostics read it).</summary>
    public const string ApiPasswordVariable = "NLTG_API_PASSWORD";

    /// <summary>The name of the wallet init container.</summary>
    public const string WalletInitContainerName = "eclair-wallet";

    /// <summary>How long the wallet init container tries before it gives up (the wallet) or lets Eclair start anyway (the sync).</summary>
    public const int WalletInitTimeoutSeconds = 180;

    /// <summary>The flag file that fails the readiness probe while the node stops (in the container, not on the PVC).</summary>
    public const string DrainFile = "/tmp/nltg-draining";

    /// <summary>
    /// How long the stop waits after failing the probe, for the Services to drop the pod (3 failed probes 1 s apart,
    /// then kube-proxy's update; see <c>ClnNode.DrainSeconds</c>).
    /// </summary>
    public const int DrainSeconds = 5;

    /// <summary>
    /// The container's command: write <c>eclair.conf</c>, then <c>exec</c> the image's start script (which execs the
    /// JVM, so SIGTERM reaches Eclair and a graceful restart is a clean shutdown).
    /// </summary>
    public static IReadOnlyList<string> StartCommand { get; } =
    [
        "/bin/sh", "-c",
        $"printf '%s\\n' \"${ConfigVariable}\" > {DataPath}/eclair.conf "
      + $"&& exec eclair-node/bin/eclair-node.sh -Declair.datadir={DataPath}"
    ];

    /// <summary>The workload of the Eclair node <paramref name="name"/>.</summary>
    public static NodeWorkload Workload(string name, EclairNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workload = new NodeWorkload(name, NodeKind.Eclair, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(DataPath, options.DataSize, Storage: options.Storage),
            Command = [.. StartCommand],
            ReadinessProbe = Probes.Exec(ReadinessCommand(options), periodSeconds: 1, timeoutSeconds: 5,
                                         failureThreshold: 3),
            // The drain (5 s) plus Eclair's own shutdown
            TerminationGracePeriodSeconds = 20,
            CustomizePod = spec => spec.Containers[0].Lifecycle = new V1Lifecycle
            {
                PreStop = new V1LifecycleHandler { Exec = new V1ExecAction { Command = [.. StopCommand] } }
            }
        };
        if (options.StartupWait is { } wait)
            workload.InitContainers.Add(wait);
        workload.InitContainers.Add(WalletInitContainer(options));
        workload.Env[ConfigVariable] = BuildConfig(name, options);
        workload.Env["JAVA_OPTS"] = options.JavaOptions;
        // For the readiness probe and the diagnostics' state commands (never on a command line)
        workload.Env[ApiPasswordVariable] = options.ApiPassword;
        workload.Ports.Add(new WorkloadPort("p2p", P2PPort));
        workload.Ports.Add(new WorkloadPort("api", ApiPort));
        return workload;
    }

    /// <summary>
    /// <c>eclair.conf</c> of <paramref name="name"/>: the Docker fixture's settings with the cluster's bitcoind alias and
    /// ports, then <see cref="EclairNodeOptions.ExtraConfig"/>.
    /// </summary>
    public static string BuildConfig(string name, EclairNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var lines = new List<string>
        {
            "eclair.chain = \"regtest\"",
            $"eclair.server.port = {Format(P2PPort)}",
            "eclair.api.enabled = true",
            "eclair.api.binding-ip = \"0.0.0.0\"",
            $"eclair.api.port = {Format(ApiPort)}",
            $"eclair.api.password = \"{options.ApiPassword}\"",
            $"eclair.bitcoind.host = \"{options.BitcoindHost}\"",
            $"eclair.bitcoind.rpcport = {Format(options.BitcoindRpcPort)}",
            $"eclair.bitcoind.rpcuser = \"{options.BitcoindRpcUser}\"",
            $"eclair.bitcoind.rpcpassword = \"{options.BitcoindRpcPassword}\"",
            $"eclair.bitcoind.wallet = \"{options.Wallet}\"",
            $"eclair.bitcoind.zmqblock = \"tcp://{options.BitcoindHost}:{Format(options.ZmqHashBlockPort)}\"",
            $"eclair.bitcoind.zmqtx = \"tcp://{options.BitcoindHost}:{Format(options.ZmqRawTxPort)}\"",
            $"eclair.node-alias = \"{options.Alias ?? name}\"",
            $"eclair.channel.min-depth-blocks = {Format(options.MinDepthBlocks)}"
        };
        lines.AddRange(options.ExtraConfig);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The wallet init script (Eclair's image has <c>curl</c> and <c>jq</c>; the settings come from the container's
    /// environment): until <c>getwalletinfo</c> on the wallet answers, <c>createwallet</c> (loaded on startup) or
    /// <c>loadwallet</c>, every 0.5 s, failing after <see cref="WalletInitTimeoutSeconds"/>; then until bitcoind's
    /// <c>initialblockdownload</c> is false (Eclair refuses a chain in IBD; on regtest it ends with the first block
    /// mined), starting anyway after the timeout. Both log how long they waited (the pod's init log).
    /// </summary>
    public static string WalletInitScript { get; } =
        """
        rpc() { curl -s --max-time 5 --user "$NLTG_RPC_AUTH" -H 'content-type: text/plain' --data-binary "{\"jsonrpc\":\"1.0\",\"id\":\"nltg\",\"method\":\"$2\",\"params\":$3}" "http://$NLTG_RPC_HOST:$NLTG_RPC_PORT/$1"; }
        start=$(date +%s); end=$((start + NLTG_TIMEOUT)); n=0
        until rpc "wallet/$NLTG_WALLET" getwalletinfo '[]' | jq -e '.error == null' >/dev/null 2>&1; do
          n=$((n + 1))
          rpc "" createwallet "{\"wallet_name\":\"$NLTG_WALLET\",\"load_on_startup\":true}" >/dev/null 2>&1
          rpc "" loadwallet "{\"filename\":\"$NLTG_WALLET\",\"load_on_startup\":true}" >/dev/null 2>&1
          if [ "$(date +%s)" -ge "$end" ]; then echo "eclair-wallet: wallet $NLTG_WALLET not ready after $NLTG_TIMEOUT s ($n tries)"; exit 1; fi
          sleep 0.5
        done
        echo "eclair-wallet: wallet $NLTG_WALLET ready after $(($(date +%s) - start)) s ($n tries)"
        until rpc "" getblockchaininfo '[]' | jq -e '.result.initialblockdownload == false' >/dev/null 2>&1; do
          if [ "$(date +%s)" -ge "$end" ]; then echo "eclair-wallet: bitcoind still in IBD, starting anyway"; exit 0; fi
          sleep 0.5
        done
        echo "eclair-wallet: bitcoind out of IBD after $(($(date +%s) - start)) s"
        """;

    /// <summary>The init container that runs <see cref="WalletInitScript"/> in Eclair's own image.</summary>
    public static V1Container WalletInitContainer(EclairNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new V1Container
        {
            Name = WalletInitContainerName,
            Image = options.Image.Reference,
            ImagePullPolicy = options.Image.PullPolicyValue,
            Command = ["/bin/sh", "-c", WalletInitScript],
            Env =
            [
                new V1EnvVar { Name = "NLTG_RPC_HOST", Value = options.BitcoindHost },
                new V1EnvVar { Name = "NLTG_RPC_PORT", Value = Format(options.BitcoindRpcPort) },
                new V1EnvVar
                {
                    Name = "NLTG_RPC_AUTH", Value = $"{options.BitcoindRpcUser}:{options.BitcoindRpcPassword}"
                },
                new V1EnvVar { Name = "NLTG_WALLET", Value = options.Wallet },
                new V1EnvVar { Name = "NLTG_TIMEOUT", Value = Format(WalletInitTimeoutSeconds) }
            ],
            Resources = WorkloadResources.Tiny.ToKubernetes()
        };
    }

    /// <summary>
    /// The <c>preStop</c> command: fail the readiness probe and wait <see cref="DrainSeconds"/>, so kube-proxy takes the
    /// pod out of its stable ClusterIP Service before the JVM stops (why: <c>ClnNode.StopCommand</c>); the kubelet's
    /// SIGTERM then stops Eclair.
    /// </summary>
    public static IReadOnlyList<string> StopCommand { get; } =
    [
        "sh", "-c", $"touch {DrainFile}; sleep {Format(DrainSeconds)}; exit 0"
    ];

    /// <summary>The readiness check: not draining, and the API answers <c>getinfo</c>.</summary>
    public static IReadOnlyList<string> ReadinessCommand(EclairNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            "sh", "-c",
            $"[ ! -f {DrainFile} ] && curl -sf --max-time 4 -u \":${ApiPasswordVariable}\" -d '' "
          + $"http://127.0.0.1:{Format(ApiPort)}/getinfo >/dev/null"
        ];
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}