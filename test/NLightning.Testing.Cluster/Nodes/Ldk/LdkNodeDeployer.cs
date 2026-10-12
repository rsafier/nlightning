namespace NLightning.Testing.Cluster.Nodes.Ldk;

using Images;
using Kube;
using Topology;

/// <summary>
/// Deploys the ldk-server nodes of a topology: first the node's <see cref="StableNodeAddress"/> Service, whose ClusterIP
/// the node announces (LDK Node announces channels only with an alias and an address), then <see cref="LdkNode"/>
/// pointed at the topology's chain node by its alias; returns once <c>get-node-info</c> answers. Peers, and the test
/// process, dial the ClusterIP: it stays the same across restarts (the pod's IP does not).
/// </summary>
public sealed class LdkNodeDeployer(Func<LdkNodeOptions, LdkNodeOptions>? customize = null) : ILightningNodeDeployer
{
    public NodeKind Kind => NodeKind.Ldk;

    /// <summary>
    /// ldk-server is deployed with the chain: its init container holds it until bitcoind answers.
    /// </summary>
    public bool DeploysWithChain => true;

    public async Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);

        var run = context.Run;
        await StableNodeAddress.EnsureAsync(run.Client, run.Identity, node.Name, NodeKind.Ldk, LdkNode.P2PPort,
                                            cancellationToken)
                               .ConfigureAwait(false);
        var clusterIp = await StableNodeAddress.ReadClusterIpAsync(run.Client, run.Identity, node.Name,
                                                                   cancellationToken)
                                               .ConfigureAwait(false);
        var options = BuildOptions(context.ChainEndpoint, node, clusterIp);
        if (customize is not null)
            options = customize(options);
        var handle = await run.DeployAsync(LdkNode.Workload(node.Name, options), context.ReadyTimeout,
                                           cancellationToken)
                              .ConfigureAwait(false);
        var peer = new LdkTestPeer(handle, clusterIp);
        context.Log?.Invoke($"[topology] {handle}: LDK {await peer.GetNodeIdAsync(cancellationToken)
                                                                   .ConfigureAwait(false)} at {clusterIp} ready");
        return peer;
    }

    /// <summary>
    /// The options of <paramref name="node"/> on <paramref name="chain"/>: the chain's startup wait, the node's storage
    /// (a PVC unless the spec says otherwise, so it can restart) and <paramref name="announcedHost"/> as its announced
    /// address. A spec's extra arguments are not ldk-server flags here: <c>key=value</c> entries replace the alias
    /// (<c>alias=...</c>) or the log level (<c>log-level=...</c>).
    /// </summary>
    public static LdkNodeOptions BuildOptions(ITopologyChainEndpoint chain, TopologyNodeSpec node,
                                              string? announcedHost)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(node);
        var options = new LdkNodeOptions
        {
            Image = node.Image ?? ImageVersions.Ldk,
            BitcoindHost = chain.RpcHost,
            BitcoindRpcPort = chain.RpcPort,
            BitcoindRpcUser = chain.RpcUser,
            BitcoindRpcPassword = chain.RpcPassword,
            AnnouncementAddresses = announcedHost is null ? [] : [$"{announcedHost}:{LdkNode.P2PPort}"],
            Storage = node.Storage ?? NodeStorage.Persistent,
            StartupWait = chain.CreateStartupWait()
        };
        foreach (var arg in node.Args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var (key, value) = separator > 0 ? (arg[..separator], arg[(separator + 1)..]) : (arg, string.Empty);
            options = key switch
            {
                "alias" => options with { Alias = value },
                "log-level" => options with { LogLevel = value },
                _ => throw new ArgumentException($"{node.Name}: unknown LDK option '{arg}' (alias=, log-level=)",
                                                 nameof(node))
            };
        }

        return options;
    }
}