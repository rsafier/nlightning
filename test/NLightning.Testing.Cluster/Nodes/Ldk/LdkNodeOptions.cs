using k8s.Models;

namespace NLightning.Testing.Cluster.Nodes.Ldk;

using Images;
using Kube;

/// <summary>
/// How an ldk-server (LDK Node) of a topology runs: its image, its bitcoind backend (ldk-server follows bitcoind over
/// RPC, polling the tip every few seconds) and the configuration <c>LdkFixture</c> writes today (an alias, so LDK Node
/// may announce channels, NL-556; an announced address; <c>Info</c> logs to stdout).
/// </summary>
public sealed record LdkNodeOptions
{
    /// <summary>The image; the version table's locally built ldk-server (commit dc02b76c) by default.</summary>
    public ImageRef Image { get; init; } = ImageVersions.Ldk;

    /// <summary>
    /// The bitcoind host ldk-server calls: the chain node's alias (its headless Service resolves inside the run's
    /// namespace).
    /// </summary>
    public required string BitcoindHost { get; init; }

    public int BitcoindRpcPort { get; init; } = 18443;

    public required string BitcoindRpcUser { get; init; }

    public required string BitcoindRpcPassword { get; init; }

    /// <summary>
    /// The node's alias in gossip; the node name when null. LDK Node opens and accepts announced channels only with an
    /// alias and listening addresses.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// The addresses LDK announces in its <c>node_announcement</c> (<c>host:port</c>), e.g. the node's stable
    /// ClusterIP (<see cref="Topology.StableNodeAddress"/>); none when empty.
    /// </summary>
    public IReadOnlyList<string> AnnouncementAddresses { get; init; } = [];

    /// <summary>ldk-server's <c>[log] level</c> (the fixture uses <c>Info</c>; the log is the pod's).</summary>
    public string LogLevel { get; init; } = "Info";

    /// <summary>The size of the data PVC (<see cref="LdkNode.DataPath"/>).</summary>
    public string DataSize { get; init; } = "1Gi";

    /// <summary>
    /// The data directory on a PVC (default, so a restart keeps the node id and its channels) or in an <c>emptyDir</c>
    /// (<see cref="NodeStorage.Ephemeral"/>: faster to start, but the node cannot be restarted or killed).
    /// </summary>
    public NodeStorage Storage { get; init; } = NodeStorage.Persistent;

    /// <summary>
    /// An init container that holds ldk-server until bitcoind answers
    /// (<see cref="Topology.ITopologyChainEndpoint.CreateStartupWait"/>), or null to start at once.
    /// </summary>
    public V1Container? StartupWait { get; init; }

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;
}