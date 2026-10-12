using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

using BitcoinCore;
using Images;
using Kube;

/// <summary>
/// How one LND node of a topology is configured: its alias, the bitcoind it follows (by the topology's plain service
/// name, so the configuration never contains the run id) and extra flags (today's
/// <c>Configuration.LNDNodes[..].Cmd.Add("--protocol.rbf-coop-close")</c>).
/// </summary>
public sealed record LndNodeOptions
{
    /// <summary>
    /// LND's switch for simple taproot channels (<see cref="ExtraArgs"/>): it advertises bits 81 (final) and 181
    /// (staging), accepts and opens <c>channel_type</c> {80} (RPC <c>CommitmentType.SIMPLE_TAPROOT_FINAL</c> = 7,
    /// private channels only) and, unless overlay channels are on too, forces LND's RBF cooperative close (bits 61/161,
    /// <c>option_simple_close</c>). Give it only to a node of its own: it changes how the node closes every channel.
    /// </summary>
    public const string SimpleTaprootChannelsFlag = "--protocol.simple-taproot-chans";

    public LndNodeOptions(string alias)
    {
        Alias = KubeNames.RequireDns1123Label(alias, "LND alias", KubeNames.MaxWorkloadNameLength);
    }

    /// <summary>The node's alias: StatefulSet, Service, container name and LND's <c>--alias</c>.</summary>
    public string Alias { get; }

    /// <summary>The image (the version table's <c>custom_lnd:0.21.4-beta</c>, never pulled).</summary>
    public ImageRef Image { get; init; } = ImageVersions.Lnd;

    /// <summary>The bitcoind Service the node follows (RPC and ZMQ).</summary>
    public string BitcoindHost { get; init; } = "miner";

    /// <summary>bitcoind's RPC port (<see cref="BitcoinCorePorts.Rpc"/>).</summary>
    public int BitcoindRpcPort { get; init; } = BitcoinCorePorts.Rpc;

    /// <summary>bitcoind's RPC user (<see cref="BitcoinCoreOptions.RpcUser"/>'s default).</summary>
    public string BitcoindRpcUser { get; init; } = "nltg";

    public string BitcoindRpcPassword { get; init; } = "nltg";

    /// <summary>bitcoind's <c>zmqpubrawblock</c> port (<see cref="BitcoinCorePorts.ZmqRawBlock"/>).</summary>
    public int ZmqRawBlockPort { get; init; } = BitcoinCorePorts.ZmqRawBlock;

    /// <summary>bitcoind's <c>zmqpubrawtx</c> port (<see cref="BitcoinCorePorts.ZmqRawTx"/>).</summary>
    public int ZmqRawTxPort { get; init; } = BitcoinCorePorts.ZmqRawTx;

    /// <summary>Whether LND accepts keysend payments (LNUnit's default, on).</summary>
    public bool AcceptKeysend { get; init; } = true;

    /// <summary>Extra LND flags, appended after the defaults (e.g. <c>--protocol.rbf-coop-close</c>).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;

    /// <summary>The size of the PVC that holds <see cref="LndWorkload.LndDir"/>.</summary>
    public string DataSize { get; init; } = "1Gi";

    /// <summary>
    /// <see cref="LndWorkload.LndDir"/> on a PVC (default) or in an <c>emptyDir</c> (<see cref="NodeStorage.Ephemeral"/>:
    /// faster to start, but the node cannot be restarted or killed).
    /// </summary>
    public NodeStorage Storage { get; init; } = NodeStorage.Persistent;

    /// <summary>
    /// An init container that holds LND until bitcoind answers (<see cref="Topology.ITopologyChainEndpoint.CreateStartupWait"/>;
    /// LND exits when its chain backend does not answer at start), or null to start at once.
    /// </summary>
    public V1Container? StartupWait { get; init; }
}