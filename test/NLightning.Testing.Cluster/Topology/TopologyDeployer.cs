using System.Diagnostics;

namespace NLightning.Testing.Cluster.Topology;

using Nodes;
using Run;

/// <summary>
/// Brings a <see cref="TopologySpec"/> up in a run: the chain node (mature wallet), every Lightning node in parallel,
/// the wallet fundings (confirmed), then the channels (opened, buried 6 deep, active on both ends).
/// </summary>
public sealed class TopologyDeployer
{
    /// <summary>The blocks mined on the fundings and on the channel opens.</summary>
    public const int ConfirmationBlocks = 6;

    private readonly TopologySpec _spec;
    private readonly IReadOnlyDictionary<NodeKind, ILightningNodeDeployer> _deployers;
    private readonly TopologyBuilder.ChainFactory _chainFactory;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _stepTimeout;
    private readonly Action<string>? _log;

    public TopologyDeployer(TopologySpec spec, IReadOnlyDictionary<NodeKind, ILightningNodeDeployer> deployers,
                            TopologyBuilder.ChainFactory chainFactory, TimeSpan readyTimeout, TimeSpan stepTimeout,
                            Action<string>? log = null)
    {
        _spec = (spec ?? throw new ArgumentNullException(nameof(spec))).EnsureValid();
        _deployers = deployers ?? throw new ArgumentNullException(nameof(deployers));
        _chainFactory = chainFactory ?? throw new ArgumentNullException(nameof(chainFactory));
        _readyTimeout = readyTimeout;
        _stepTimeout = stepTimeout;
        _log = log;
    }

    public async Task<TestTopology> DeployAsync(TestRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var log = _log;
        var watch = Stopwatch.StartNew();

        // Chain
        var chain = await _chainFactory(run, _spec.ChainNode, _readyTimeout, cancellationToken).ConfigureAwait(false);
        log?.Invoke($"[topology] {run.Namespace}: chain {chain.Node.Name} ready at {Elapsed(watch)}");

        // Lightning nodes, in parallel
        var context = new TopologyDeployContext(run, chain, _readyTimeout, log);
        var deployed = await Task.WhenAll(_spec.LightningNodes.Select(async spec =>
                                  {
                                      var deployer = _deployers.TryGetValue(spec.Kind, out var d)
                                                         ? d
                                                         : throw new InvalidOperationException(
                                                               $"No deployer for {spec.Kind} ({spec.Name})");
                                      var node = await deployer.DeployAsync(context, spec, cancellationToken)
                                                               .ConfigureAwait(false);
                                      return (spec.Name, Node: node);
                                  }))
                                 .ConfigureAwait(false);
        var nodes = deployed.ToDictionary(d => d.Name, d => d.Node, StringComparer.Ordinal);
        await WaitAllAtTipAsync(chain, nodes.Values, _stepTimeout, cancellationToken).ConfigureAwait(false);
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
                await TopologyPoll.UntilAsync(async ct =>
                {
                    var balance = await nodes[name].GetConfirmedBalanceSatAsync(ct).ConfigureAwait(false);
                    return balance >= amount ? null : $"{balance} of {amount} sat confirmed";
                }, _stepTimeout, $"{name}'s wallet funded", cancellationToken).ConfigureAwait(false);
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

            log?.Invoke($"[topology] {run.Namespace}: {channels.Count} channels active at {Elapsed(watch)}");
        }

        return new TestTopology(_spec, run, chain, nodes, channels, _stepTimeout);
    }

    /// <summary>Waits until every node in <paramref name="nodes"/> has processed the chain's tip, and returns it.</summary>
    public static async Task<long> WaitAllAtTipAsync(ITopologyChain chain, IEnumerable<ITopologyLightningNode> nodes,
                                                     TimeSpan timeout, CancellationToken cancellationToken)
    {
        var list = nodes.ToList();
        long tip = 0;
        await TopologyPoll.UntilAsync(async ct =>
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
        await TopologyPoll.UntilAsync(async ct =>
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
        TopologyPoll.UntilAsync(async ct =>
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