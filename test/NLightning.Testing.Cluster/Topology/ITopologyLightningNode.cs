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

    /// <summary>
    /// Whether <see cref="DeployAsync"/> may run while the chain node is still starting (the topology's first wave):
    /// it then reads only <see cref="TopologyDeployContext.ChainEndpoint"/> (and awaits
    /// <see cref="TopologyDeployContext.WaitForChainAsync"/> for anything else), and the node tolerates a chain that
    /// does not answer yet (<see cref="ITopologyChainEndpoint.CreateStartupWait"/>). False (the default) deploys it
    /// once the chain is ready, with <see cref="TopologyDeployContext.Chain"/> set.
    /// </summary>
    bool DeploysWithChain => false;

    /// <summary>Deploys <paramref name="node"/> and returns once it is ready.</summary>
    Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                             CancellationToken cancellationToken);
}

/// <summary>
/// What a deployer gets: the run, the chain backend to point the node at (its <see cref="ChainEndpoint"/> at once, the
/// started <see cref="Chain"/> once it is ready), the ready timeout and the log.
/// </summary>
public sealed class TopologyDeployContext
{
    private readonly Task<ITopologyChain> _chain;

    /// <summary>A context for a deployer that runs once <paramref name="chain"/> is ready.</summary>
    public TopologyDeployContext(Run.TestRun run, ITopologyChain chain, TimeSpan readyTimeout, Action<string>? log)
        : this(run, chain ?? throw new ArgumentNullException(nameof(chain)), Task.FromResult(chain), readyTimeout, log)
    {
    }

    /// <summary>
    /// A context for a deployer that runs while the chain starts (<see cref="ILightningNodeDeployer.DeploysWithChain"/>):
    /// <paramref name="chain"/> completes when the chain is ready.
    /// </summary>
    public TopologyDeployContext(Run.TestRun run, ITopologyChainEndpoint chainEndpoint, Task<ITopologyChain> chain,
                                 TimeSpan readyTimeout, Action<string>? log)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        ChainEndpoint = chainEndpoint ?? throw new ArgumentNullException(nameof(chainEndpoint));
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));
        ReadyTimeout = readyTimeout;
        Log = log;
    }

    public Run.TestRun Run { get; }

    /// <summary>Where the node reaches the chain (host, ports, credentials, startup wait); set from the start.</summary>
    public ITopologyChainEndpoint ChainEndpoint { get; }

    /// <summary>The started chain.</summary>
    /// <exception cref="InvalidOperationException">The chain is still starting (or failed): await
    /// <see cref="WaitForChainAsync"/>.</exception>
    public ITopologyChain Chain =>
        _chain.IsCompletedSuccessfully
            ? _chain.Result
            : throw new InvalidOperationException(
                $"The chain {ChainEndpoint.RpcHost} is not ready yet: a deployer with "
              + $"{nameof(ILightningNodeDeployer.DeploysWithChain)} reads {nameof(ChainEndpoint)} or awaits "
              + $"{nameof(WaitForChainAsync)}");

    /// <summary>Whether <see cref="Chain"/> is set (the chain is ready).</summary>
    public bool IsChainReady => _chain.IsCompletedSuccessfully;

    /// <summary>The started chain, once it is ready.</summary>
    public Task<ITopologyChain> WaitForChainAsync(CancellationToken cancellationToken) =>
        _chain.WaitAsync(cancellationToken);

    public TimeSpan ReadyTimeout { get; }

    public Action<string>? Log { get; }
}