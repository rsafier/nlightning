namespace NLightning.Testing.Cluster.Nodes.Eclair;

using Images;
using Kube;
using Topology;

/// <summary>
/// Deploys the Eclair nodes of a topology: <see cref="EclairNode"/> pointed at the topology's chain node by its alias
/// (RPC, a wallet of its own, ZMQ <c>hashblock</c> and <c>rawtx</c>), returned once its API answers; peers dial it at
/// its <see cref="StableNodeAddress"/>, so its address survives a restart.
/// </summary>
/// <remarks>Eclair 0.14.3 refuses Bitcoin Core older than 31: declare the chain node with
/// <see cref="ImageVersions.BitcoinCore31"/>.</remarks>
public sealed class EclairNodeDeployer(Func<EclairNodeOptions, EclairNodeOptions>? customize = null)
    : ILightningNodeDeployer
{
    public NodeKind Kind => NodeKind.Eclair;

    /// <summary>
    /// Eclair is deployed with the chain: its pod (and PVC) start while bitcoind starts, and its init containers hold
    /// the JVM until bitcoind answers, its wallet exists and the chain left its initial block download.
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
        var handle = await context.Run.DeployAsync(EclairNode.Workload(node.Name, options), context.ReadyTimeout,
                                                   cancellationToken)
                                  .ConfigureAwait(false);
        var p2pHost = await StableNodeAddress.EnsureAsync(context.Run.Client, context.Run.Identity, node.Name,
                                                          NodeKind.Eclair, EclairNode.P2PPort, cancellationToken)
                                             .ConfigureAwait(false);
        var peer = new EclairTestPeer(handle, options.ApiPassword, p2pHost);
        context.Log?.Invoke($"[topology] {handle}: Eclair {await peer.GetNodeIdAsync(cancellationToken)
                                                                       .ConfigureAwait(false)} ready");
        return peer;
    }

    /// <summary>
    /// The options of <paramref name="node"/> on <paramref name="chain"/>: its alias, the chain's RPC and ZMQ feeds and
    /// startup wait, the node's storage; the node's topology args are more <c>eclair.conf</c> lines.
    /// </summary>
    /// <exception cref="InvalidOperationException">The chain publishes no <c>hashblock</c> feed.</exception>
    public static EclairNodeOptions BuildOptions(ITopologyChainEndpoint chain, TopologyNodeSpec node)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(node);
        return new EclairNodeOptions
        {
            Image = node.Image ?? ImageVersions.Eclair,
            BitcoindHost = chain.RpcHost,
            BitcoindRpcPort = chain.RpcPort,
            BitcoindRpcUser = chain.RpcUser,
            BitcoindRpcPassword = chain.RpcPassword,
            ZmqHashBlockPort = chain.ZmqHashBlockPort
                            ?? throw new InvalidOperationException(
                                   $"The chain {chain.RpcHost} publishes no ZMQ hashblock feed, which Eclair follows"),
            ZmqRawTxPort = chain.ZmqRawTxPort,
            ExtraConfig = node.Args,
            Storage = node.Storage ?? NodeStorage.Persistent,
            StartupWait = chain.CreateStartupWait()
        };
    }
}