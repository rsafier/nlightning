namespace NLightning.Testing.Cluster.Topology.Lnd;

using Images;
using Kube;

/// <summary>How <see cref="LndRegtestNetwork"/> deploys its <see cref="Spec"/>.</summary>
public sealed record LndRegtestNetworkOptions
{
    /// <summary>The nodes and channels (the Docker fixture's network by default).</summary>
    public LndRegtestNetworkSpec Spec { get; init; } = LndRegtestNetworkSpec.Default;

    /// <summary>The LND image; null for the version table's (<see cref="ImageVersions.Lnd"/>, LND 0.21.4).</summary>
    public ImageRef? LndImage { get; init; }

    /// <summary>
    /// Where bitcoind and the LND nodes keep their data: a PVC by default, so <see cref="LndRegtestNetwork.RestartAsync"/>
    /// works (a restarted node keeps its wallet and channels). <see cref="NodeStorage.Ephemeral"/> starts faster but
    /// refuses restarts.
    /// </summary>
    public NodeStorage Storage { get; init; } = NodeStorage.Persistent;

    /// <summary>How long a node may take to become ready (also after a restart).</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>How long each step of the set-up (fundings, opens, the active and graph waits) may take.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Where the build writes its progress (with timings); null for nowhere.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// More of the topology, declared after the network's nodes (e.g. in-process NLightning nodes with their
    /// deployer); those nodes start with the network, but the network's set-up leaves them alone.
    /// </summary>
    public Action<TopologyBuilder>? ConfigureBuilder { get; init; }

    /// <summary>
    /// Wait until every startup channel is in every LND's graph with both policies (and the funder's set) before the
    /// network is handed out (default true): LND 0.21 lists a fresh channel active before its router can use it
    /// (NL-319).
    /// </summary>
    public bool WaitForGraph { get; init; } = true;
}