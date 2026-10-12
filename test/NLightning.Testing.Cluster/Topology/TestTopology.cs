namespace NLightning.Testing.Cluster.Topology;

using Nodes;
using Run;

/// <summary>
/// A deployed topology: its chain, its Lightning nodes by alias and the channels it opened. The run owns the objects;
/// disposing the run deletes them. Disposing the topology closes the nodes' clients (LND's gRPC channels).
/// </summary>
public sealed class TestTopology : IDisposable
{
    private readonly IReadOnlyDictionary<string, ITopologyLightningNode> _nodes;

    internal TestTopology(TopologySpec spec, TestRun run, ITopologyChain chain,
                          IReadOnlyDictionary<string, ITopologyLightningNode> nodes,
                          IReadOnlyList<TopologyChannel> channels, TimeSpan stepTimeout,
                          IReadOnlyDictionary<string, TimeSpan>? timings = null)
    {
        Timings = timings ?? new Dictionary<string, TimeSpan>();
        Spec = spec;
        Run = run;
        Chain = chain;
        _nodes = nodes;
        Channels = channels;
        StepTimeout = stepTimeout;
    }

    public TopologySpec Spec { get; }

    public TestRun Run { get; }

    public ITopologyChain Chain { get; }

    /// <summary>The Lightning nodes by alias.</summary>
    public IReadOnlyDictionary<string, ITopologyLightningNode> Nodes => _nodes;

    /// <summary>The pre-opened channels, active on both ends.</summary>
    public IReadOnlyList<TopologyChannel> Channels { get; }

    /// <summary>
    /// When each step of the build finished, from the start of the build: <c>chain</c>, <c>nodes</c> (every Lightning
    /// node ready and at the tip), <c>fundings</c> and <c>channels</c> (the last two only when the topology has them).
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings { get; }

    /// <summary>How long each wait of the helpers below may take.</summary>
    public TimeSpan StepTimeout { get; }

    /// <summary>The Lightning node named <paramref name="name"/>.</summary>
    public ITopologyLightningNode Node(string name) =>
        _nodes.TryGetValue(name, out var node)
            ? node
            : throw new KeyNotFoundException($"The topology has no Lightning node named {name}");

    /// <summary>The Lightning node named <paramref name="name"/> as its implementation's adapter.</summary>
    public T Node<T>(string name) where T : class, ITopologyLightningNode =>
        Node(name) as T ?? throw new InvalidCastException($"{name} is a {Node(name).Kind} node, not a {typeof(T).Name}");

    /// <summary>Mines <paramref name="blocks"/> blocks and waits until every Lightning node is at the tip.</summary>
    public async Task<long> MineAndSyncAsync(int blocks, CancellationToken cancellationToken)
    {
        await Chain.MineAsync(blocks, cancellationToken).ConfigureAwait(false);
        return await WaitAllAtTipAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Has every channel's funder connect to its peer again by the peer's Service name (after a restart moved a pod to
    /// another IP: implementations keep the address they resolved), then waits until each channel is active on both
    /// ends.
    /// </summary>
    public async Task ReconnectChannelsAsync(CancellationToken cancellationToken)
    {
        foreach (var channel in Channels)
        {
            var from = Node(channel.Spec.From);
            var to = Node(channel.Spec.To);
            await TopologyDeployer.ConnectAsync(from, await to.GetAddressAsync(cancellationToken)
                                                              .ConfigureAwait(false),
                                                StepTimeout, cancellationToken)
                                  .ConfigureAwait(false);
        }

        await WaitChannelsActiveAsync(StepTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until every pre-opened channel is active on both ends.</summary>
    public async Task WaitChannelsActiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var channel in Channels)
        {
            var from = Node(channel.Spec.From);
            var to = Node(channel.Spec.To);
            var fromId = await from.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
            var toId = await to.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
            await TopologyDeployer.WaitChannelActiveAsync(from, toId, channel.Open.FundingTxId, timeout,
                                                          cancellationToken)
                                  .ConfigureAwait(false);
            await TopologyDeployer.WaitChannelActiveAsync(to, fromId, channel.Open.FundingTxId, timeout,
                                                          cancellationToken)
                                  .ConfigureAwait(false);
        }
    }

    /// <summary>Waits until every Lightning node has processed the chain's tip, and returns it.</summary>
    public Task<long> WaitAllAtTipAsync(CancellationToken cancellationToken) =>
        TopologyDeployer.WaitAllAtTipAsync(Chain, _nodes.Values, StepTimeout, cancellationToken);

    public void Dispose()
    {
        foreach (var node in _nodes.Values.OfType<IDisposable>())
            node.Dispose();
    }
}

/// <summary>A channel the topology opened: its declaration and its funding outpoint.</summary>
public sealed record TopologyChannel(TopologyChannelSpec Spec, TestChannelOpen Open, string? ShortChannelId);