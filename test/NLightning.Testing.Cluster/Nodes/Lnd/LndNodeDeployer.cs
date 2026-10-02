namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Images;
using Kube;
using Topology;

/// <summary>
/// Deploys the LND nodes of a topology: <see cref="LndNode"/> pointed at the topology's chain node by its alias (RPC and
/// the ZMQ feeds), returned once LND is synced to the chain and its gRPC connection is up. Peers dial it by its alias
/// (the headless Service); after a restart, redial it by its new pod IP (<c>LndPairTopology.RestartAsync</c>): LND
/// keeps the IP it resolved.
/// </summary>
public sealed class LndNodeDeployer(Func<LndNodeOptions, LndNodeOptions>? customize = null) : ILightningNodeDeployer
{
    public NodeKind Kind => NodeKind.Lnd;

    /// <summary>
    /// LND is deployed with the chain: its pod (and PVC) start while bitcoind starts, its init container holds LND
    /// until bitcoind answers, and it turns ready (<c>synced_to_chain</c>) once the chain has mined its first blocks.
    /// </summary>
    public bool DeploysWithChain => true;

    public async Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        var options = BuildOptions(context.ChainEndpoint, node);
        if (customize is not null)
            options = customize(options);
        var lnd = await LndNode.DeployAsync(context.Run, options, context.ReadyTimeout, cancellationToken)
                               .ConfigureAwait(false);
        context.Log?.Invoke($"[topology] {lnd.Handle}: LND {await lnd.GetNodeIdAsync(cancellationToken)
                                                                       .ConfigureAwait(false)} ready");
        return lnd;
    }

    /// <summary>
    /// The options of <paramref name="node"/> on <paramref name="chain"/>, with the chain's startup wait and the node's
    /// storage.
    /// </summary>
    public static LndNodeOptions BuildOptions(ITopologyChainEndpoint chain, TopologyNodeSpec node)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(node);
        return new LndNodeOptions(node.Name)
        {
            Image = node.Image ?? ImageVersions.Lnd,
            BitcoindHost = chain.RpcHost,
            BitcoindRpcPort = chain.RpcPort,
            BitcoindRpcUser = chain.RpcUser,
            BitcoindRpcPassword = chain.RpcPassword,
            ZmqRawBlockPort = chain.ZmqRawBlockPort,
            ZmqRawTxPort = chain.ZmqRawTxPort,
            ExtraArgs = node.Args,
            Storage = node.Storage ?? NodeStorage.Persistent,
            StartupWait = chain.CreateStartupWait()
        };
    }
}