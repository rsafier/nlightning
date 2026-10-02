namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Images;
using Kube;

/// <summary>
/// How one LND node of a topology is configured: its alias, the bitcoind it follows (by the topology's plain service
/// name, so the configuration never contains the run id) and extra flags (today's
/// <c>Configuration.LNDNodes[..].Cmd.Add("--protocol.rbf-coop-close")</c>).
/// </summary>
public sealed record LndNodeOptions
{
    public LndNodeOptions(string alias)
    {
        Alias = KubeNames.RequireDns1123Label(alias, "LND alias", KubeNames.MaxWorkloadNameLength);
    }

    /// <summary>The node's alias: StatefulSet, Service, container name and LND's <c>--alias</c>.</summary>
    public string Alias { get; }

    /// <summary>The image (the version table's <c>custom_lnd:latest</c>, never pulled).</summary>
    public ImageRef Image { get; init; } = ImageVersions.Lnd;

    /// <summary>The bitcoind Service the node follows (RPC and ZMQ).</summary>
    public string BitcoindHost { get; init; } = "miner";

    /// <summary>bitcoind's RPC port (regtest default).</summary>
    public int BitcoindRpcPort { get; init; } = 18443;

    public string BitcoindRpcUser { get; init; } = "bitcoin";

    public string BitcoindRpcPassword { get; init; } = "bitcoin";

    /// <summary>bitcoind's <c>zmqpubrawblock</c> port (LNUnit's 28334).</summary>
    public int ZmqRawBlockPort { get; init; } = 28334;

    /// <summary>bitcoind's <c>zmqpubrawtx</c> port (LNUnit's 28335).</summary>
    public int ZmqRawTxPort { get; init; } = 28335;

    /// <summary>Whether LND accepts keysend payments (LNUnit's default, on).</summary>
    public bool AcceptKeysend { get; init; } = true;

    /// <summary>Extra LND flags, appended after the defaults (e.g. <c>--protocol.rbf-coop-close</c>).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;

    /// <summary>The size of the PVC that holds <see cref="LndWorkload.LndDir"/>.</summary>
    public string DataSize { get; init; } = "1Gi";
}