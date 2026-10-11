using System.Globalization;
using System.Text;
using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.Ldk;

using Kube;

/// <summary>
/// The ldk-server (LDK Node over gRPC) workload: the locally built <c>nltg-ldk-server</c> image (no registry, pulled
/// never) as a StatefulSet with its data (keys, channel monitors, the BDK wallet) on a PVC at <see cref="DataPath"/>, so
/// a restart keeps the node id and its channels. The configuration <c>LdkFixture</c> copies into its container is
/// written by the container itself from <see cref="ConfigVariable"/> at every start (<see cref="StartCommand"/>), at
/// the same path (<see cref="ConfigPath"/>), so <c>ldk-server-cli -c</c> works as on Docker. Ready when
/// <c>ldk-server-cli get-node-info</c> answers and the node is not draining (see <see cref="StopCommand"/>); being at
/// the chain's tip is the caller's wait, not the probe's.
/// </summary>
public static class LdkNode
{
    /// <summary>The data volume's mount path (the configuration and the storage directory live under it).</summary>
    public const string DataPath = "/data";

    /// <summary>The configuration file ldk-server starts with and <c>ldk-server-cli -c</c> reads.</summary>
    public const string ConfigPath = DataPath + "/config.toml";

    /// <summary>LDK Node's storage directory (<c>[storage.disk] dir_path</c>).</summary>
    public const string StoragePath = DataPath + "/ldk";

    /// <summary>The environment variable that carries the configuration into the container.</summary>
    public const string ConfigVariable = "NLTG_LDK_CONFIG";

    public const string Network = "regtest";

    public const int P2PPort = 9735;

    /// <summary>ldk-server's gRPC port (the image's <c>LDK_SERVER_NODE_GRPC_SERVICE_ADDRESS</c>); used by the CLI only.</summary>
    public const int GrpcPort = 3536;

    /// <summary>The flag file that fails the readiness probe while the node stops (in the container, not on the PVC).</summary>
    public const string DrainFile = "/tmp/nltg-draining";

    /// <summary>
    /// How long the stop waits after failing the probe, for the Services to drop the pod (3 failed probes 1 s apart,
    /// then kube-proxy's update), as <see cref="Cln.ClnNode.DrainSeconds"/>.
    /// </summary>
    public const int DrainSeconds = 5;

    /// <summary>The workload of the ldk-server node <paramref name="name"/>.</summary>
    public static NodeWorkload Workload(string name, LdkNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workload = new NodeWorkload(name, NodeKind.Ldk, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(DataPath, options.DataSize, Storage: options.Storage),
            Command = [.. StartCommand],
            ReadinessProbe = Probes.Exec(ReadinessCommand, periodSeconds: 1, timeoutSeconds: 5, failureThreshold: 3),
            TerminationGracePeriodSeconds = 15,
            // Drain first: a peer's redial through the stable ClusterIP must not reach the Service between the pod's
            // network going away and kube-proxy's update (see StopCommand)
            CustomizePod = spec => spec.Containers[0].Lifecycle = new V1Lifecycle
            {
                PreStop = new V1LifecycleHandler { Exec = new V1ExecAction { Command = [.. StopCommand] } }
            }
        };
        if (options.StartupWait is { } wait)
            workload.InitContainers.Add(wait);
        workload.Env[ConfigVariable] = BuildConfig(name, options);
        workload.Ports.Add(new WorkloadPort("p2p", P2PPort));
        workload.Ports.Add(new WorkloadPort("grpc", GrpcPort));
        return workload;
    }

    /// <summary>
    /// The <c>config.toml</c> of the node (the layout of <c>LdkFixture</c>'s, with the chain's alias as the bitcoind
    /// host).
    /// </summary>
    public static string BuildConfig(string name, LdkNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = new StringBuilder();
        config.Append(CultureInfo.InvariantCulture, $"""
                                                    [node]
                                                    network = "{Network}"
                                                    listening_addresses = ["0.0.0.0:{P2PPort}"]

                                                    """);
        if (options.AnnouncementAddresses.Count > 0)
            config.Append(CultureInfo.InvariantCulture,
                          $"announcement_addresses = [{string.Join(", ", options.AnnouncementAddresses.Select(Quote))}]\n");
        config.Append(CultureInfo.InvariantCulture, $"""
                                                    alias = {Quote(options.Alias ?? name)}

                                                    [storage.disk]
                                                    dir_path = "{StoragePath}"

                                                    [log]
                                                    level = {Quote(options.LogLevel)}
                                                    log_to_file = false

                                                    [bitcoind]
                                                    rpc_address = {Quote($"{options.BitcoindHost}:{options.BitcoindRpcPort}")}
                                                    rpc_user = {Quote(options.BitcoindRpcUser)}
                                                    rpc_password = {Quote(options.BitcoindRpcPassword)}
                                                    """);
        return config.ToString();
    }

    /// <summary>
    /// The container's command: writes <see cref="ConfigVariable"/> to <see cref="ConfigPath"/> (the same file at every
    /// start), then runs ldk-server on it as PID 1, so it gets the kubelet's SIGTERM.
    /// </summary>
    public static IReadOnlyList<string> StartCommand { get; } =
    [
        "sh", "-c",
        $"mkdir -p {DataPath} && printf '%s' \"${ConfigVariable}\" > {ConfigPath} && exec ldk-server {ConfigPath}"
    ];

    /// <summary>The <c>ldk-server-cli</c> command line of <paramref name="subcommand"/> in the node's container.</summary>
    public static IReadOnlyList<string> CliCommand(string subcommand, IEnumerable<string>? args = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subcommand);
        List<string> command = ["ldk-server-cli", "-c", ConfigPath, subcommand];
        if (args is not null)
            command.AddRange(args);
        return command;
    }

    /// <summary>The readiness check: not draining, and <c>get-node-info</c> answers.</summary>
    public static IReadOnlyList<string> ReadinessCommand { get; } =
    [
        "sh", "-c", $"[ ! -f {DrainFile} ] && ldk-server-cli -c {ConfigPath} get-node-info >/dev/null"
    ];

    /// <summary>
    /// The <c>preStop</c> command: fails the readiness probe and waits <see cref="DrainSeconds"/>, so that kube-proxy
    /// removes the pod from its stable ClusterIP Service before the process stops (a redial that reaches the Service in
    /// between is pinned by conntrack to the vanished pod IP and hangs). The kubelet then sends SIGTERM to ldk-server.
    /// </summary>
    public static IReadOnlyList<string> StopCommand { get; } =
    [
        "sh", "-c", $"touch {DrainFile}; sleep {DrainSeconds}; exit 0"
    ];

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
      + "\"";
}