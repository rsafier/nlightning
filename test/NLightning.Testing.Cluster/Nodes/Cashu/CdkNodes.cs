using System.Globalization;

namespace NLightning.Testing.Cluster.Nodes.Cashu;

using Images;
using Kube;

/// <summary>
/// CDK's mint daemon <c>cdk-mintd</c> and wallet CLI <c>cdk-cli</c> as nodes of a run (the Cashu mint proof, Cashu plan
/// C2, NL-993): the mint uses a CDK payment processor outside the cluster (<c>backend = "grpcprocessor"</c>, plain
/// HTTP/2), the wallet is a pod that idles until the test runs <c>cdk-cli</c> in it.
/// </summary>
public static class CdkNodes
{
    /// <summary>The mint's alias (StatefulSet, Service and container name).</summary>
    public const string MintName = "cdk-mintd";

    /// <summary>The wallet's alias.</summary>
    public const string WalletName = "cdk-wallet";

    /// <summary>The mint's HTTP port in its pod.</summary>
    public const int MintPort = 8085;

    /// <summary>The mint's work directory (its configuration and database, an <c>emptyDir</c>).</summary>
    public const string MintDataPath = "/data";

    /// <summary>The wallet pod's directory for wallet work directories (an <c>emptyDir</c>).</summary>
    public const string WalletDataPath = "/wallet";

    /// <summary>The variable that carries the mint's configuration file into its pod.</summary>
    public const string ConfigVariable = "NLTG_MINTD_CONFIG";

    /// <summary>The variable the mint's configuration reads its mnemonic from (<c>env:CDK_MINTD_MNEMONIC</c>).</summary>
    public const string MnemonicVariable = "CDK_MINTD_MNEMONIC";

    /// <summary>The mint's URL inside the run's namespace (what the wallet uses and the mint announces).</summary>
    public static string MintServiceUrl => $"http://{MintName}:{MintPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The mint's <c>config.toml</c> (CDK 0.18): sqlite, sat, the processor at
    /// <paramref name="processorHost"/>:<paramref name="processorPort"/> without TLS (<c>allow_insecure</c>, which
    /// cdk-mintd 0.18 requires for a plaintext processor, loopback included), the mnemonic from
    /// <see cref="MnemonicVariable"/>.
    /// </summary>
    public static string MintConfig(string processorHost, int processorPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorPort);
        return string.Create(CultureInfo.InvariantCulture, $"""
            [info]
            url = "{MintServiceUrl}/"
            listen_host = "0.0.0.0"
            listen_port = {MintPort}
            mnemonic = "env:{MnemonicVariable}"

            [database]
            engine = "sqlite"

            [payment_backend]
            backend = "grpcprocessor"
            unit = "sat"

            [grpc_processor]
            address = "{processorHost}"
            port = {processorPort}
            supported_units = ["sat"]
            allow_insecure = true

            """);
    }

    /// <summary>
    /// The mint: writes <see cref="ConfigVariable"/> to <c>config.toml</c>, loads it into a new mint
    /// (<c>config init --new-mint</c>, CDK 0.18 keeps its configuration in its database) and runs it; ready once its
    /// HTTP port accepts connections.
    /// </summary>
    public static NodeWorkload MintWorkload(string config, string mnemonic, ImageRef? image = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);
        var workload = new NodeWorkload(MintName, NodeKind.Other, image ?? ImageVersions.CdkMintd)
        {
            Command =
            [
                "sh", "-c",
                $"printf '%s' \"${ConfigVariable}\" > {MintDataPath}/config.toml "
              + $"&& cdk-mintd -w {MintDataPath} config init --file {MintDataPath}/config.toml --new-mint "
              + $"&& exec cdk-mintd -w {MintDataPath} --enable-logging"
            ],
            ReadinessProbe = Probes.Tcp(MintPort, periodSeconds: 1),
            TerminationGracePeriodSeconds = 3
        };
        workload.Env[ConfigVariable] = config;
        workload.Env[MnemonicVariable] = mnemonic;
        workload.ScratchVolumes["data"] = MintDataPath;
        workload.Ports.Add(new WorkloadPort("http", MintPort));
        return workload;
    }

    /// <summary>The wallet: <c>cdk-cli</c>'s image idling (<c>sleep infinity</c>) for <c>kubectl exec</c>.</summary>
    public static NodeWorkload WalletWorkload(ImageRef? image = null)
    {
        var workload = new NodeWorkload(WalletName, NodeKind.Other, image ?? ImageVersions.CdkCli)
        {
            Command = ["sleep", "infinity"],
            Resources = new WorkloadResources("50m", "64Mi", "1", "512Mi"),
            TerminationGracePeriodSeconds = 1
        };
        workload.ScratchVolumes["wallet"] = WalletDataPath;
        return workload;
    }
}