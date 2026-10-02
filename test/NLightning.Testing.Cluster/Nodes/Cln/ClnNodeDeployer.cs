namespace NLightning.Testing.Cluster.Nodes.Cln;

using Images;
using Topology;

/// <summary>
/// Deploys the CLN nodes of a topology: <see cref="ClnNode"/> pointed at the topology's chain node by its alias, then
/// waits until it is ready (getinfo answers); peers dial it at its <see cref="StableNodeAddress"/>.
/// </summary>
public sealed class ClnNodeDeployer(Func<ClnNodeOptions, ClnNodeOptions>? customize = null) : ILightningNodeDeployer
{
    public NodeKind Kind => NodeKind.Cln;

    public async Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        var options = BuildOptions(context.Chain, node);
        if (customize is not null)
            options = customize(options);
        var handle = await context.Run.DeployAsync(ClnNode.Workload(node.Name, options), context.ReadyTimeout,
                                                   cancellationToken)
                                  .ConfigureAwait(false);
        var p2pHost = await StableNodeAddress.EnsureAsync(context.Run.Client, context.Run.Identity, node.Name,
                                                          NodeKind.Cln, ClnNode.P2PPort, cancellationToken)
                                             .ConfigureAwait(false);
        var peer = new ClnTestPeer(handle, p2pHost);
        context.Log?.Invoke($"[topology] {handle}: CLN {await peer.GetNodeIdAsync(cancellationToken)
                                                                   .ConfigureAwait(false)} ready");
        return peer;
    }

    /// <summary>The options of <paramref name="node"/> on <paramref name="chain"/>.</summary>
    public static ClnNodeOptions BuildOptions(ITopologyChain chain, TopologyNodeSpec node)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(node);
        return new ClnNodeOptions
        {
            Image = node.Image ?? ImageVersions.Cln,
            BitcoindHost = chain.RpcHost,
            BitcoindRpcPort = chain.RpcPort,
            BitcoindRpcUser = chain.RpcUser,
            BitcoindRpcPassword = chain.RpcPassword,
            ExtraArgs = node.Args
        };
    }
}