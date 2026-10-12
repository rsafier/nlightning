using System.Diagnostics;

namespace NLightning.Testing.Cluster.Topology;

using Kube;
using Nodes;
using Run;

/// <summary>
/// Brings a <see cref="TopologySpec"/> up in a run: the chain node (mature wallet) and, in the same wave, every
/// Lightning node whose deployer allows it (<see cref="ILightningNodeDeployer.DeploysWithChain"/>; the others once the
/// chain is ready), then the wallet fundings (confirmed), then the channels (opened, buried 6 deep, active on both
/// ends). One wave instead of two saves a PVC wait (about 6 s on OrbStack) and the nodes' own start.
/// </summary>
public sealed class TopologyDeployer
{
    /// <summary>The blocks mined on the fundings and on the channel opens.</summary>
    public const int ConfirmationBlocks = 6;

    /// <summary>The default <see cref="ChainAddressWait"/> for a chain node on <see cref="NodeStorage.Ephemeral"/> storage.</summary>
    public static readonly TimeSpan DefaultChainAddressWait = TimeSpan.FromSeconds(5);

    private readonly TopologySpec _spec;
    private readonly IReadOnlyDictionary<NodeKind, ILightningNodeDeployer> _deployers;
    private readonly TopologyBuilder.ChainFactory _chainFactory;
    private readonly TopologyBuilder.ChainEndpointFactory? _chainEndpointFactory;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _stepTimeout;
    private readonly Action<string>? _log;

    /// <param name="chainEndpointFactory">
    /// The chain's endpoint before it is up, so the Lightning nodes can be deployed with it; null deploys them once the
    /// chain is ready.
    /// </param>
    public TopologyDeployer(TopologySpec spec, IReadOnlyDictionary<NodeKind, ILightningNodeDeployer> deployers,
                            TopologyBuilder.ChainFactory chainFactory, TimeSpan readyTimeout, TimeSpan stepTimeout,
                            Action<string>? log = null,
                            TopologyBuilder.ChainEndpointFactory? chainEndpointFactory = null)
    {
        _spec = (spec ?? throw new ArgumentNullException(nameof(spec))).EnsureValid();
        _deployers = deployers ?? throw new ArgumentNullException(nameof(deployers));
        _chainFactory = chainFactory ?? throw new ArgumentNullException(nameof(chainFactory));
        _chainEndpointFactory = chainEndpointFactory;
        _readyTimeout = readyTimeout;
        _stepTimeout = stepTimeout;
        _log = log;
    }

    /// <summary>
    /// How long the nodes deployed with the chain wait for the chain's name to resolve before they start (see
    /// <see cref="TopologyBuilder.ChainAddressWait"/>); null for the default by the chain node's storage.
    /// </summary>
    public TimeSpan? ChainAddressWait { get; init; }

    public async Task<TestTopology> DeployAsync(TestRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var log = _log;
        var watch = Stopwatch.StartNew();
        var timings = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        void Mark(string phase) => timings[phase] = watch.Elapsed;

        // Chain and Lightning nodes: those that may start with the chain at once, the others once it is ready
        var deployers = _spec.LightningNodes.ToDictionary(n => n.Name, n => _deployers.TryGetValue(n.Kind, out var d)
                                                              ? d
                                                              : throw new InvalidOperationException(
                                                                    $"No deployer for {n.Kind} ({n.Name})"),
                                                          StringComparer.Ordinal);
        var endpoint = _chainEndpointFactory?.Invoke(_spec.ChainNode);
        var early = endpoint is null
                        ? []
                        : _spec.LightningNodes.Where(n => deployers[n.Name].DeploysWithChain).ToList();
        var late = _spec.LightningNodes.Except(early).ToList();

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var chainTask = DeployChainAsync(run, watch, Mark, abort.Token);
        var addressWait = ChainAddressWait
                       ?? (_spec.ChainNode.Storage == NodeStorage.Ephemeral ? DefaultChainAddressWait : TimeSpan.Zero);
        if (early.Count > 0 && addressWait > TimeSpan.Zero)
        {
            var resolved = await WaitForChainAddressAsync(run, endpoint!, addressWait, chainTask, abort.Token)
                               .ConfigureAwait(false);
            log?.Invoke($"[topology] {run.Namespace}: chain {endpoint!.RpcHost} "
                      + (resolved ? "has an address" : $"has no address after {addressWait.TotalSeconds:F0} s")
                      + $" at {Elapsed(watch)}");
        }

        var earlyTasks = early.Select(spec =>
                                  DeployNodeAsync(deployers[spec.Name],
                                                  new TopologyDeployContext(run, endpoint!, chainTask, _readyTimeout,
                                                                            log), spec, abort.Token))
                              .ToList();
        var lateTask = DeployLateAsync(run, chainTask, late, deployers, abort.Token);
        if (early.Count > 0)
            log?.Invoke($"[topology] {run.Namespace}: {early.Count} Lightning node(s) deployed with the chain");

        var chain = await AwaitAllFailFastAsync(chainTask, [.. earlyTasks, lateTask], abort).ConfigureAwait(false);
        var nodes = earlyTasks.Select(t => t.Result).Concat(lateTask.Result)
                              .ToDictionary(d => d.Name, d => d.Node, StringComparer.Ordinal);
        try
        {
            await WaitAllAtTipAsync(chain, nodes.Values, _stepTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DisposeAll(nodes.Values);
            throw;
        }

        Mark("nodes");
        log?.Invoke($"[topology] {run.Namespace}: {nodes.Count} Lightning nodes ready at {Elapsed(watch)}");

        // Fundings
        if (_spec.Fundings.Count > 0)
        {
            var expected = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var funding in _spec.Fundings)
            {
                var node = nodes[funding.Node];
                var before = expected.TryGetValue(funding.Node, out var e)
                                 ? e
                                 : await node.GetConfirmedBalanceSatAsync(cancellationToken).ConfigureAwait(false);
                var address = await node.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
                await chain.SendToAddressAsync(address, funding.AmountSat, cancellationToken).ConfigureAwait(false);
                expected[funding.Node] = before + funding.AmountSat;
            }

            await chain.MineAsync(ConfirmationBlocks, cancellationToken).ConfigureAwait(false);
            await WaitAllAtTipAsync(chain, nodes.Values, _stepTimeout, cancellationToken).ConfigureAwait(false);
            foreach (var (name, amount) in expected)
                await Poll.UntilDoneAsync(async ct =>
                {
                    var balance = await nodes[name].GetConfirmedBalanceSatAsync(ct).ConfigureAwait(false);
                    return balance >= amount ? null : $"{balance} of {amount} sat confirmed";
                }, _stepTimeout, $"{name}'s wallet funded", cancellationToken).ConfigureAwait(false);
            Mark("fundings");
            log?.Invoke($"[topology] {run.Namespace}: {_spec.Fundings.Count} wallets funded at {Elapsed(watch)}");
        }

        // Channels: one funder at a time (each open spends that funder's wallet), then all confirmed at once
        var opens = new List<(TopologyChannelSpec Spec, TestChannelOpen Open)>();
        foreach (var channel in _spec.Channels)
        {
            var from = nodes[channel.From];
            var to = nodes[channel.To];
            var toAddress = await to.GetAddressAsync(cancellationToken).ConfigureAwait(false);
            await ConnectAsync(from, toAddress, _stepTimeout, cancellationToken).ConfigureAwait(false);
            var open = await from.OpenChannelAsync(new TestOpenChannelRequest(toAddress.NodeId, channel.CapacitySat,
                                                                              channel.PushMsat, channel.Announce),
                                                   cancellationToken)
                                 .ConfigureAwait(false);
            opens.Add((channel, open));
        }

        var channels = new List<TopologyChannel>();
        if (opens.Count > 0)
        {
            await chain.MineAsync(ConfirmationBlocks, cancellationToken).ConfigureAwait(false);
            await WaitAllAtTipAsync(chain, nodes.Values, _stepTimeout, cancellationToken).ConfigureAwait(false);
            foreach (var (spec, open) in opens)
            {
                var from = nodes[spec.From];
                var to = nodes[spec.To];
                var fromId = await from.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
                var toId = await to.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
                await WaitChannelActiveAsync(from, toId, open.FundingTxId, _stepTimeout, cancellationToken)
                   .ConfigureAwait(false);
                await WaitChannelActiveAsync(to, fromId, open.FundingTxId, _stepTimeout, cancellationToken)
                   .ConfigureAwait(false);
                var listed = (await from.ListChannelsAsync(cancellationToken).ConfigureAwait(false))
                   .First(c => c.FundingTxId == open.FundingTxId);
                channels.Add(new TopologyChannel(spec, open, listed.ShortChannelId));
            }

            Mark("channels");
            log?.Invoke($"[topology] {run.Namespace}: {channels.Count} channels active at {Elapsed(watch)}");
        }

        return new TestTopology(_spec, run, chain, nodes, channels, _stepTimeout, timings);
    }

    /// <summary>
    /// Waits until the chain's Service has an address (or <paramref name="timeout"/>, or the chain failed: its error
    /// surfaces with the wave's).
    /// </summary>
    internal static async Task<bool> WaitForChainAddressAsync(TestRun run, ITopologyChainEndpoint endpoint,
                                                             TimeSpan timeout, Task chainTask,
                                                             CancellationToken cancellationToken)
    {
        var wait = run.Client.WaitForServiceAddressAsync(run.Namespace, endpoint.RpcHost, timeout, cancellationToken);
        await Task.WhenAny(wait, chainTask).ConfigureAwait(false);
        if (!wait.IsCompleted)
            return false;

        try
        {
            return await wait.ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // The API answered badly: the nodes start anyway, as without the wait
        }
    }

    private async Task<ITopologyChain> DeployChainAsync(TestRun run, Stopwatch watch, Action<string> mark,
                                                        CancellationToken cancellationToken)
    {
        var chain = await _chainFactory(run, _spec.ChainNode, _readyTimeout, cancellationToken).ConfigureAwait(false);
        mark("chain");
        _log?.Invoke($"[topology] {run.Namespace}: chain {chain.Node.Name} ready at {Elapsed(watch)}");
        return chain;
    }

    private static async Task<(string Name, ITopologyLightningNode Node)> DeployNodeAsync(
        ILightningNodeDeployer deployer, TopologyDeployContext context, TopologyNodeSpec spec,
        CancellationToken cancellationToken)
    {
        var node = await deployer.DeployAsync(context, spec, cancellationToken).ConfigureAwait(false);
        return (spec.Name, node);
    }

    private async Task<(string Name, ITopologyLightningNode Node)[]> DeployLateAsync(
        TestRun run, Task<ITopologyChain> chainTask, IReadOnlyList<TopologyNodeSpec> late,
        IReadOnlyDictionary<string, ILightningNodeDeployer> deployers, CancellationToken cancellationToken)
    {
        if (late.Count == 0)
            return [];

        var chain = await chainTask.ConfigureAwait(false);
        var context = new TopologyDeployContext(run, chain, _readyTimeout, _log);
        return await Task.WhenAll(late.Select(spec => DeployNodeAsync(deployers[spec.Name], context, spec,
                                                                      cancellationToken)))
                         .ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits the chain and every node deployment; the first failure cancels the others (a node waiting for a chain
    /// that failed would otherwise wait for its whole ready timeout), and the first real error (not the cancellations
    /// it caused) is thrown, after the nodes that did come up are disposed.
    /// </summary>
    private static async Task<ITopologyChain> AwaitAllFailFastAsync(Task<ITopologyChain> chainTask,
                                                                     IReadOnlyList<Task> nodeTasks,
                                                                     CancellationTokenSource abort)
    {
        Task[] all = [chainTask, .. nodeTasks];
        foreach (var task in all)
            FailFast.CancelOnFault(task, abort);
        try
        {
            await Task.WhenAll(all).ConfigureAwait(false);
            return chainTask.Result;
        }
        catch
        {
            await FailFast.SettleAsync(all).ConfigureAwait(false);
            foreach (var task in nodeTasks.OfType<Task<(string Name, ITopologyLightningNode Node)>>()
                                          .Where(t => t.IsCompletedSuccessfully))
                DisposeAll([task.Result.Node]);
            foreach (var task in nodeTasks.OfType<Task<(string Name, ITopologyLightningNode Node)[]>>()
                                          .Where(t => t.IsCompletedSuccessfully))
                DisposeAll(task.Result.Select(r => r.Node));

            if (FailFast.FirstRealError(all) is { } error)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            throw;
        }
    }

    private static void DisposeAll(IEnumerable<ITopologyLightningNode> nodes)
    {
        foreach (var node in nodes.OfType<IDisposable>())
            node.Dispose();
    }

    /// <summary>Waits until every node in <paramref name="nodes"/> has processed the chain's tip, and returns it.</summary>
    public static async Task<long> WaitAllAtTipAsync(ITopologyChain chain, IEnumerable<ITopologyLightningNode> nodes,
                                                     TimeSpan timeout, CancellationToken cancellationToken)
    {
        var list = nodes.ToList();
        long tip = 0;
        await Poll.UntilDoneAsync(async ct =>
        {
            tip = await chain.GetBlockCountAsync(ct).ConfigureAwait(false);
            var heights = await Task.WhenAll(list.Select(async n =>
                                                 (n.Alias, Height: await n.GetBlockHeightAsync(ct)
                                                                          .ConfigureAwait(false))))
                                    .ConfigureAwait(false);
            var behind = heights.Where(h => h.Height != tip).ToList();
            return behind.Count == 0
                       ? null
                       : $"tip {tip}, " + string.Join(", ", behind.Select(h => $"{h.Alias} at {h.Height}"));
        }, timeout, "every node at the tip", cancellationToken).ConfigureAwait(false);
        return tip;
    }

    /// <summary>
    /// Connects <paramref name="node"/> to <paramref name="peer"/>, retrying each failed or stuck attempt (20 s each)
    /// until <paramref name="timeout"/>: a peer that is briefly out of its Service (a slow readiness probe on a busy
    /// cluster) refuses the dial.
    /// </summary>
    public static async Task ConnectAsync(ILightningTestPeer node, TestPeerAddress peer, TimeSpan timeout,
                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        string? lastError = null;
        await Poll.UntilDoneAsync(async ct =>
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await node.ConnectAsync(peer, attempt.Token).ConfigureAwait(false);
                return null;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                lastError = e.Message;
                return lastError;
            }
        }, timeout, $"{node.Alias} connected to {peer}", cancellationToken, TimeSpan.FromSeconds(1))
                          .ConfigureAwait(false);
    }

    /// <summary>Waits until <paramref name="node"/> lists the channel with <paramref name="fundingTxId"/> as active.</summary>
    public static Task WaitChannelActiveAsync(ILightningTestPeer node, string remoteNodeId, string fundingTxId,
                                              TimeSpan timeout, CancellationToken cancellationToken) =>
        Poll.UntilDoneAsync(async ct =>
        {
            var channels = await node.ListChannelsAsync(ct).ConfigureAwait(false);
            var channel = channels.FirstOrDefault(c => c.FundingTxId == fundingTxId && c.RemoteNodeId == remoteNodeId);
            return channel switch
            {
                null => "not listed",
                { Active: false } => "listed, not active",
                _ => null
            };
        }, timeout, $"{node.Alias}'s channel {fundingTxId} active", cancellationToken);

    private static string Elapsed(Stopwatch watch) => $"{watch.Elapsed.TotalSeconds:F1} s";
}