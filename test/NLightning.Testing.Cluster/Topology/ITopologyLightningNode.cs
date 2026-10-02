namespace NLightning.Testing.Cluster.Topology;

using Nodes;

/// <summary>
/// A Lightning node of a built topology: the uniform <see cref="ILightningTestPeer"/> facade plus what the topology
/// needs to bring it to a known state (at the tip, wallet funded).
/// </summary>
public interface ITopologyLightningNode : ILightningTestPeer
{
    /// <summary>The block height the node has processed.</summary>
    Task<long> GetBlockHeightAsync(CancellationToken cancellationToken);

    /// <summary>The node's confirmed, spendable on-chain balance.</summary>
    Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deploys the Lightning nodes of one <see cref="NodeKind"/> into a run. The CLN one is
/// <see cref="Nodes.Cln.ClnNodeDeployer"/>; the other implementations register theirs with
/// <see cref="TopologyBuilder.UseDeployer"/>.
/// </summary>
public interface ILightningNodeDeployer
{
    NodeKind Kind { get; }

    /// <summary>Deploys <paramref name="node"/> and returns once it is ready.</summary>
    Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                             CancellationToken cancellationToken);
}

/// <summary>What a deployer gets: the run, the chain backend to point the node at, and the ready timeout.</summary>
public sealed record TopologyDeployContext(Run.TestRun Run, ITopologyChain Chain, TimeSpan ReadyTimeout,
                                           Action<string>? Log);