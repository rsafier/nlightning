using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using Grpc.Core;

namespace NLightning.Testing.Cluster.Topology.Lnd;

using Diagnostics;
using Nodes;
using Nodes.Lnd;
using Run;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;

/// <summary>
/// The LND regtest network of the Docker suites' <c>LightningRegtestNetworkFixture</c> on the cluster harness (test
/// harness phase 3): bitcoind <c>miner</c> and the LND nodes of an <see cref="LndRegtestNetworkSpec"/>
/// (<see cref="LndRegtestNetworkSpec.Default"/>: alice, bob, carol with their channels, david without), on
/// <c>custom_lnd:0.21.4-beta</c> with LNUnit's flags. Like LNUnit, every LND node is connected to every other one
/// (permanent peers), gets two 42.69 BTC wallet outputs, and the funders open public channels at 10 sat/vB with their
/// push and set their side to 0 msat / 0 ppm / delta 40.
/// </summary>
/// <remarks>
/// <para>
/// Handed out only when it is usable: every LND <c>SERVER_ACTIVE</c> and synced, every startup channel active on both
/// ends and in every LND's graph with both policies (LND 0.21 lists a fresh channel active before its router can use
/// it, NL-319; <see cref="LndGraph"/>). Clients are the in-tree <see cref="LndNodeConnection"/>s
/// (<see cref="GetLndNode"/>, <see cref="LndNodes"/>), built from the <c>tls.cert</c> and <c>admin.macaroon</c> read out
/// of each pod, the certificate pinned; each dials its pod's DNS name, so it stays the same object across
/// <see cref="RestartAsync"/>.
/// </para>
/// <para>
/// A restart is a StatefulSet restart (same pod name, DNS name and PVC; a new pod IP), so the Docker fixture's
/// address-hold containers (NL-262) have no equivalent: <see cref="RestartAsync"/> has the node's peers dial its new
/// pod IP (LND keeps the IP it resolved for a peer) and waits until its channels are active again.
/// </para>
/// <para>
/// Other nodes join with <see cref="JoinAsync"/> (any <see cref="ILightningNodeDeployer"/>: our in-process node
/// through <c>InProcessNodeDeployer</c> in the integration tests) and <see cref="OpenChannelAsync"/>.
/// </para>
/// </remarks>
public sealed class LndRegtestNetwork : IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long <see cref="ConnectPermanentAsync"/> waits for LND's background dial before it dials again.</summary>
    private static readonly TimeSpan s_redialInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<LndNode> _nodes;
    private readonly ConcurrentDictionary<string, ITopologyLightningNode> _joined = new(StringComparer.Ordinal);
    private readonly bool _ownsTopology;
    private int _disposed;

    private LndRegtestNetwork(TestTopology topology, LndRegtestNetworkOptions options, IReadOnlyList<LndNode> nodes,
                              IReadOnlyList<LndRegtestChannel> channels, IReadOnlyDictionary<string, TimeSpan> timings,
                              bool ownsTopology)
    {
        Topology = topology;
        Options = options;
        _nodes = nodes;
        Channels = channels;
        Timings = timings;
        _ownsTopology = ownsTopology;
        Chain = topology.Chain as BitcoinCoreTopologyChain
             ?? throw new InvalidOperationException("The LND regtest network runs on a BitcoinCoreTopologyChain");
    }

    /// <summary>The network's nodes and chain as a topology (with any nodes <see cref="LndRegtestNetworkOptions.ConfigureBuilder"/> added).</summary>
    public TestTopology Topology { get; }

    public TestRun Run => Topology.Run;

    public LndRegtestNetworkOptions Options { get; }

    public LndRegtestNetworkSpec Spec => Options.Spec;

    /// <summary>bitcoind <c>miner</c> and its chain helpers (<see cref="BitcoinCoreTopologyChain.Chain"/>).</summary>
    public BitcoinCoreTopologyChain Chain { get; }

    /// <summary>The startup channels, active on both ends and in every graph when the network was handed out.</summary>
    public IReadOnlyList<LndRegtestChannel> Channels { get; }

    /// <summary>
    /// When each step finished, from the start of the topology build: the topology's <c>chain</c> and <c>nodes</c>,
    /// then <c>server-active</c>, <c>reserve</c>, <c>peers</c>, <c>fundings</c>, <c>opens</c>, <c>active</c>,
    /// <c>policies</c> and <c>graph</c>.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings { get; }

    /// <summary>The LND nodes in the spec's order.</summary>
    public IReadOnlyList<LndNode> Nodes => _nodes;

    /// <summary>The in-tree gRPC connections of the LND nodes, in the spec's order (the fixture's <c>LndNodes</c>).</summary>
    public IReadOnlyList<LndNodeConnection> LndNodes => _nodes.Select(n => n.Connection).ToList();

    /// <summary>The nodes that joined after the build (<see cref="JoinAsync"/>), by alias.</summary>
    public IReadOnlyDictionary<string, ITopologyLightningNode> JoinedNodes => _joined;

    /// <summary>The LND node <paramref name="alias"/>.</summary>
    public LndNode Node(string alias) =>
        _nodes.FirstOrDefault(n => n.Alias == alias)
     ?? throw new KeyNotFoundException($"The LND regtest network has no LND node named {alias}");

    /// <summary>The in-tree gRPC connection of the LND node <paramref name="alias"/> (the fixture's <c>GetLndNode</c>).</summary>
    public LndNodeConnection GetLndNode(string alias) => Node(alias).Connection;

    /// <summary>Any Lightning node of the network: an LND node, a node of the topology or a joined one.</summary>
    public ITopologyLightningNode Peer(string alias) =>
        _joined.TryGetValue(alias, out var joined)
            ? joined
            : Topology.Nodes.TryGetValue(alias, out var node)
                ? node
                : throw new KeyNotFoundException($"The LND regtest network has no node named {alias}");

    /// <summary>Every Lightning node: the topology's and the joined ones.</summary>
    public IEnumerable<ITopologyLightningNode> AllNodes => Topology.Nodes.Values.Concat(_joined.Values);

    /// <summary>
    /// Declares the network's chain and LND nodes on <paramref name="builder"/> (the nodes start in the chain's wave),
    /// then <see cref="LndRegtestNetworkOptions.ConfigureBuilder"/>. No fundings or channels: <see cref="SetUpAsync"/>
    /// does those the LND way.
    /// </summary>
    public static void Declare(TopologyBuilder builder, LndRegtestNetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        var spec = options.Spec.EnsureValid();

        builder.Log ??= options.Log;
        builder.ReadyTimeout = options.ReadyTimeout;
        builder.StepTimeout = options.StepTimeout;
        builder.Storage = options.Storage;
        builder.AddBitcoinCore(spec.ChainName);
        foreach (var node in spec.Nodes)
            builder.AddLnd(node.Alias, options.LndImage, node.Args);
        options.ConfigureBuilder?.Invoke(builder);
    }

    /// <summary>
    /// Builds the network into <paramref name="run"/>: the topology (<see cref="Declare"/>), then
    /// <see cref="SetUpAsync"/>. The network owns the topology (disposing it closes the clients); the run's disposal
    /// deletes the pods.
    /// </summary>
    public static async Task<LndRegtestNetwork> BuildAsync(TestRun run, LndRegtestNetworkOptions options,
                                                           CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new TopologyBuilder();
        Declare(builder, options);
        var topology = await builder.BuildAsync(run, cancellationToken).ConfigureAwait(false);
        try
        {
            return await SetUpAsync(topology, options, ownsTopology: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            topology.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Brings a built topology of <see cref="Declare"/> to the Docker fixture's state: every LND
    /// <c>SERVER_ACTIVE</c>, the miner's reserve matured, every LND connected to every other one, the wallets funded,
    /// the channels opened, confirmed and active on both ends, the funders' policies set, and (with
    /// <see cref="LndRegtestNetworkOptions.WaitForGraph"/>) every channel in every LND's graph. The caller keeps
    /// owning <paramref name="topology"/> (a warm fixture's). A failure dumps the run's diagnostics.
    /// </summary>
    public static Task<LndRegtestNetwork> SetUpAsync(TestTopology topology, LndRegtestNetworkOptions options,
                                                     CancellationToken cancellationToken) =>
        SetUpAsync(topology, options, ownsTopology: false, cancellationToken);

    private static Task<LndRegtestNetwork> SetUpAsync(TestTopology topology, LndRegtestNetworkOptions options,
                                                      bool ownsTopology, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(options);
        return topology.Run.CaptureOnFailureAsync("lnd regtest network set-up",
                                                  () => new SetUp(topology, options).RunAsync(ownsTopology,
                                                      cancellationToken));
    }

    /// <summary>Mines <paramref name="blocks"/> blocks and waits until every node (joined ones too) is at the tip.</summary>
    public async Task<long> MineAndSyncAsync(int blocks, CancellationToken cancellationToken)
    {
        await Chain.MineAsync(blocks, cancellationToken).ConfigureAwait(false);
        return await WaitAllAtTipAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until every node (joined ones too) has processed the chain's tip, and returns it.</summary>
    public Task<long> WaitAllAtTipAsync(CancellationToken cancellationToken) =>
        TopologyDeployer.WaitAllAtTipAsync(Chain, AllNodes, Options.StepTimeout, cancellationToken);

    /// <summary>
    /// Waits until every LND is <c>SERVER_ACTIVE</c> and every startup channel is active on both ends (a test that
    /// disturbed the network restores it with this).
    /// </summary>
    public async Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        foreach (var node in _nodes)
            await node.WaitServerActiveAsync(Options.ReadyTimeout, cancellationToken).ConfigureAwait(false);
        foreach (var channel in Channels)
            await WaitActiveBothEndsAsync(Peer(channel.Spec.From), Peer(channel.Spec.To), channel.FundingTxId,
                                          Options.StepTimeout, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Restarts the LND node <paramref name="alias"/> on its PVC (graceful, or with <paramref name="kill"/> a 1 s
    /// grace), waits until it is <c>SERVER_ACTIVE</c> and synced, has every node of the network it was connected to
    /// dial it again (LND peers at its new pod IP, the others at its alias) and waits until every channel it had active
    /// with a node of the network is active again on both ends. Its <see cref="LndNodeConnection"/> stays the same
    /// object. Channels with nodes the network does not know (a node a test started by itself) are left to that node.
    /// </summary>
    public async Task<LndRestartResult> RestartAsync(string alias, bool kill = false,
                                                     CancellationToken cancellationToken = default)
    {
        var node = Node(alias);
        var watch = Stopwatch.StartNew();
        var nodeId = await node.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
        var podUidBefore = node.Handle.PodUid;
        var podIpBefore = node.Handle.PodIp;
        var peers = (await node.Lightning.ListPeersAsync(new ListPeersRequest(), cancellationToken: cancellationToken)
                               .ResponseAsync.ConfigureAwait(false)).Peers.Select(p => p.PubKey).ToHashSet();
        var channels = (await node.ListChannelsAsync(cancellationToken).ConfigureAwait(false))
                      .Where(c => c.Active).ToList();
        var known = await NodesByIdAsync(cancellationToken).ConfigureAwait(false);
        known.Remove(nodeId);

        if (kill)
            await node.KillAsync(Options.ReadyTimeout, cancellationToken).ConfigureAwait(false);
        else
            await node.RestartAsync(Options.ReadyTimeout, cancellationToken).ConfigureAwait(false);
        var serverActive = watch.Elapsed;

        var podIp = node.Handle.PodIp ?? throw new InvalidOperationException($"{alias} has no pod IP");
        var redial = peers.Concat(channels.Select(c => c.RemoteNodeId)).Distinct()
                          .Where(known.ContainsKey).Select(id => known[id]).ToList();
        foreach (var peer in redial)
        {
            if (peer is LndNode lnd)
                await ConnectPermanentAsync(lnd, new TestPeerAddress(nodeId, podIp, LndWorkload.P2pPort),
                                            Options.StepTimeout, cancellationToken)
                    .ConfigureAwait(false);
            else
                await TopologyDeployer.ConnectAsync(peer, await node.GetAddressAsync(cancellationToken)
                                                                    .ConfigureAwait(false),
                                                    Options.StepTimeout, cancellationToken)
                                      .ConfigureAwait(false);
        }

        var awaited = channels.Where(c => known.ContainsKey(c.RemoteNodeId)).ToList();
        foreach (var channel in awaited)
            await WaitActiveBothEndsAsync(node, known[channel.RemoteNodeId], channel.FundingTxId, Options.StepTimeout,
                                          cancellationToken)
                .ConfigureAwait(false);

        var result = new LndRestartResult(alias, kill, podUidBefore, node.Handle.PodUid, podIpBefore, podIp,
                                          serverActive, watch.Elapsed, redial.Count, awaited.Count);
        Log($"[lnd-network] {Run.Namespace}: {result}");
        return result;
    }

    /// <summary>
    /// Deploys <paramref name="node"/> with <paramref name="deployer"/> on the network's chain (our in-process node
    /// through <c>InProcessNodeDeployer</c>), waits until it is at the tip and, with <paramref name="fundSat"/>, funds
    /// its wallet (confirmed). The deployer keeps owning the node (dispose it before the run).
    /// </summary>
    public async Task<ITopologyLightningNode> JoinAsync(ILightningNodeDeployer deployer, TopologyNodeSpec node,
                                                        long fundSat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployer);
        ArgumentNullException.ThrowIfNull(node);
        if (node.Kind != deployer.Kind)
            throw new ArgumentException($"{node.Name} is a {node.Kind} node, the deployer deploys {deployer.Kind}",
                                        nameof(node));
        if (Topology.Nodes.ContainsKey(node.Name) || node.Name == Spec.ChainName || _joined.ContainsKey(node.Name))
            throw new ArgumentException($"The network already has a node named {node.Name}", nameof(node));

        var watch = Stopwatch.StartNew();
        var context = new TopologyDeployContext(Run, Chain, Options.ReadyTimeout, Options.Log);
        var joined = await deployer.DeployAsync(context, node, cancellationToken).ConfigureAwait(false);
        if (!_joined.TryAdd(node.Name, joined))
            throw new InvalidOperationException($"A node named {node.Name} joined twice");

        await WaitAllAtTipAsync(cancellationToken).ConfigureAwait(false);
        if (fundSat > 0)
        {
            var before = await joined.GetConfirmedBalanceSatAsync(cancellationToken).ConfigureAwait(false);
            var address = await joined.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
            await Chain.SendToAddressAsync(address, fundSat, cancellationToken).ConfigureAwait(false);
            await MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, cancellationToken).ConfigureAwait(false);
            await Poll.UntilDoneAsync(async ct =>
            {
                var balance = await joined.GetConfirmedBalanceSatAsync(ct).ConfigureAwait(false);
                return balance >= before + fundSat ? null : $"{balance} of {before + fundSat} sat confirmed";
            }, Options.StepTimeout, $"{node.Name}'s wallet funded", cancellationToken).ConfigureAwait(false);
        }

        Log($"[lnd-network] {Run.Namespace}: {node.Name} ({node.Kind}) joined in {watch.Elapsed.TotalSeconds:F1} s"
          + (fundSat > 0 ? $", wallet funded with {fundSat} sat" : string.Empty));
        return joined;
    }

    /// <summary>
    /// Opens a channel from <paramref name="from"/> to the node <paramref name="to"/> (any node of the network):
    /// <paramref name="from"/> dials, opens and the funding is mined 6 deep; returns once the channel is active on both
    /// ends and every LND end has its own edge in its graph (a public channel: once every LND of the network has it
    /// with both policies).
    /// </summary>
    public async Task<LndRegtestChannel> OpenChannelAsync(ITopologyLightningNode from, string to, long capacitySat,
                                                          long pushMsat, bool announce,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        var peer = Peer(to);
        var peerAddress = await peer.GetAddressAsync(cancellationToken).ConfigureAwait(false);
        await TopologyDeployer.ConnectAsync(from, peerAddress, Options.StepTimeout, cancellationToken)
                              .ConfigureAwait(false);
        var open = await from.OpenChannelAsync(new TestOpenChannelRequest(peerAddress.NodeId, capacitySat, pushMsat,
                                                                          announce),
                                               cancellationToken)
                             .ConfigureAwait(false);
        await Chain.Chain.WaitForMempoolAsync(open.FundingTxId, cancellationToken, Options.StepTimeout)
                   .ConfigureAwait(false);
        await MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, cancellationToken).ConfigureAwait(false);
        await WaitActiveBothEndsAsync(from, peer, open.FundingTxId, Options.StepTimeout, cancellationToken)
            .ConfigureAwait(false);

        var spec = new LndRegtestChannelSpec(from.Alias, to, capacitySat, pushMsat / 1000);
        var chanId = await ChanIdAsync(from, peer, open.FundingTxId, cancellationToken).ConfigureAwait(false);
        var channel = new LndRegtestChannel(spec, open.FundingTxId, open.OutputIndex ?? -1, chanId);
        if (announce)
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [from.Alias] = await from.GetNodeIdAsync(cancellationToken).ConfigureAwait(false)
            };
            await WaitInEveryGraphAsync(_nodes, [channel], ids, withPolicy: false, Options.StepTimeout,
                                        cancellationToken)
                .ConfigureAwait(false);
        }
        else
            foreach (var lnd in new[] { from, peer }.OfType<LndNode>())
                await Poll.UntilAsync(ct => LndGraph.HasOwnChannelEdgeAsync(lnd, chanId, ct), Options.StepTimeout,
                                      s_pollInterval, $"{lnd.Alias} has its own edge of {channel}", cancellationToken)
                          .ConfigureAwait(false);

        return channel;
    }

    /// <summary>
    /// The LND node <paramref name="payer"/> pays an invoice of the last of <paramref name="hops"/> along exactly that
    /// route (LND's <c>BuildRoute</c> over the hops' node ids, <paramref name="outgoingChanId"/> pinning the first
    /// channel, then <c>SendToRouteV2</c>), retrying a failed attempt until <paramref name="timeout"/> (NL-319).
    /// </summary>
    public async Task<LndRoutedPayment> PayAlongAsync(string payer, IReadOnlyList<string> hops, long amountMsat,
                                                      ulong? outgoingChanId, TimeSpan timeout,
                                                      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hops);
        if (hops.Count == 0)
            throw new ArgumentException("A route needs at least the payee", nameof(hops));

        var from = Node(payer);
        var payee = Node(hops[^1]);
        var hopKeys = new List<ByteString>();
        foreach (var hop in hops)
            hopKeys.Add(ByteString.CopyFrom(Convert.FromHexString(await Node(hop).GetNodeIdAsync(cancellationToken)
                                                                                  .ConfigureAwait(false))));
        var invoice = await payee.Lightning.AddInvoiceAsync(new Invoice
        {
            ValueMsat = amountMsat,
            Memo = $"{payer} via {string.Join(" > ", hops)} {Guid.NewGuid():N}"
        }, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        var decoded = await from.Lightning.DecodePayReqAsync(new PayReqString { PayReq = invoice.PaymentRequest },
                                                             cancellationToken: cancellationToken)
                                .ResponseAsync.ConfigureAwait(false);

        var deadline = DateTime.UtcNow + timeout;
        var attempts = 0;
        string? failure = null;
        while (true)
        {
            attempts++;
            try
            {
                var request = new BuildRouteRequest
                {
                    AmtMsat = amountMsat,
                    FinalCltvDelta = checked((int)decoded.CltvExpiry),
                    OutgoingChanId = outgoingChanId ?? 0,
                    PaymentAddr = invoice.PaymentAddr
                };
                request.HopPubkeys.AddRange(hopKeys);
                var route = (await from.Router.BuildRouteAsync(request, cancellationToken: cancellationToken)
                                       .ResponseAsync.ConfigureAwait(false)).Route;
                var attempt = await from.Router.SendToRouteV2Async(new SendToRouteRequest
                {
                    PaymentHash = invoice.RHash,
                    Route = route
                }, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
                var channels = route.Hops.Select(h => h.ChanId).ToList();
                if (attempt.Status == HTLCAttempt.Types.HTLCStatus.Succeeded)
                    return new LndRoutedPayment(true, attempts, channels, null, LndMapping.ToHex(invoice.RHash));

                failure = attempt.Failure is { } f
                              ? $"{f.Code} at hop {f.FailureSourceIndex}"
                              : attempt.Status.ToString();
            }
            catch (RpcException e) when (!cancellationToken.IsCancellationRequested)
            {
                failure = $"{e.StatusCode}: {e.Status.Detail}";
            }

            if (DateTime.UtcNow >= deadline)
                return new LndRoutedPayment(false, attempts, [], failure, LndMapping.ToHex(invoice.RHash));

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var node in _joined.Values.OfType<IDisposable>())
            node.Dispose();
        if (_ownsTopology)
            Topology.Dispose();
    }

    /// <summary>
    /// <paramref name="node"/> dials <paramref name="peer"/> as a permanent peer (LNUnit's <c>perm</c>: LND keeps
    /// reconnecting), retrying until <paramref name="timeout"/>; returns once LND lists the peer.
    /// </summary>
    internal static Task ConnectPermanentAsync(LndNode node, TestPeerAddress peer, TimeSpan timeout,
                                               CancellationToken cancellationToken)
    {
        var last = string.Empty;
        var lastDial = DateTime.MinValue;
        return Poll.UntilDoneAsync(async ct =>
        {
            var peers = await node.Lightning.ListPeersAsync(new ListPeersRequest(), cancellationToken: ct)
                                  .ResponseAsync.ConfigureAwait(false);
            if (peers.Peers.Any(p => p.PubKey == peer.NodeId))
                return null;

            // LND answers a permanent connect at once and dials in the background: dial again only after a while
            if (DateTime.UtcNow - lastDial < s_redialInterval)
                return $"not listed yet {last}";

            lastDial = DateTime.UtcNow;
            try
            {
                await node.Lightning.ConnectPeerAsync(new ConnectPeerRequest
                {
                    Addr = new LightningAddress { Pubkey = peer.NodeId, Host = $"{peer.Host}:{peer.Port}" },
                    Perm = true,
                    Timeout = 10
                }, deadline: DateTime.UtcNow.AddSeconds(20), cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException e) when (e.Status.Detail.Contains("already connected", StringComparison.Ordinal))
            {
                return null;
            }
            catch (RpcException e) when (!ct.IsCancellationRequested)
            {
                last = $"{e.StatusCode}: {e.Status.Detail}";
            }

            return $"not listed yet {last}";
        }, timeout, $"{node.Alias} connected to {peer}", cancellationToken, TimeSpan.FromMilliseconds(250));
    }

    private void Log(string line) => Options.Log?.Invoke(line);

    private async Task<Dictionary<string, ITopologyLightningNode>> NodesByIdAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, ITopologyLightningNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in AllNodes)
            byId[await node.GetNodeIdAsync(cancellationToken).ConfigureAwait(false)] = node;
        return byId;
    }

    private static async Task WaitActiveBothEndsAsync(ITopologyLightningNode a, ITopologyLightningNode b,
                                                      string fundingTxId, TimeSpan timeout,
                                                      CancellationToken cancellationToken)
    {
        var aId = await a.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
        var bId = await b.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
        await TopologyDeployer.WaitChannelActiveAsync(a, bId, fundingTxId, timeout, cancellationToken)
                              .ConfigureAwait(false);
        await TopologyDeployer.WaitChannelActiveAsync(b, aId, fundingTxId, timeout, cancellationToken)
                              .ConfigureAwait(false);
    }

    /// <summary>LND's <c>chan_id</c> of the channel, read from an LND end.</summary>
    private static async Task<ulong> ChanIdAsync(ITopologyLightningNode a, ITopologyLightningNode b,
                                                 string fundingTxId, CancellationToken cancellationToken)
    {
        var lnd = a as LndNode ?? b as LndNode
               ?? throw new InvalidOperationException($"Neither {a.Alias} nor {b.Alias} is an LND node");
        var channels = await lnd.Lightning.ListChannelsAsync(new ListChannelsRequest(),
                                                             cancellationToken: cancellationToken)
                                .ResponseAsync.ConfigureAwait(false);
        var channel = channels.Channels.FirstOrDefault(c => c.ChannelPoint.StartsWith(fundingTxId + ":",
                                                                                     StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"{lnd.Alias} does not list the channel {fundingTxId}");
        return channel.ChanId;
    }

    /// <summary>
    /// Until every LND of <paramref name="nodes"/> has every channel in its graph with both policies enabled (and,
    /// with <paramref name="withPolicy"/>, the funder's side at the spec's policy). <paramref name="nodeIds"/> maps the
    /// funders' aliases to their node ids.
    /// </summary>
    private static Task WaitInEveryGraphAsync(IReadOnlyList<LndNode> nodes, IReadOnlyList<LndRegtestChannel> channels,
                                              IReadOnlyDictionary<string, string> nodeIds, bool withPolicy,
                                              TimeSpan timeout, CancellationToken cancellationToken) =>
        Poll.UntilDoneAsync(async ct =>
        {
            var missing = new List<string>();
            foreach (var node in nodes)
                foreach (var channel in channels)
                {
                    var edge = await LndGraph.GetEdgeAsync(node, channel.ChanId, ct).ConfigureAwait(false);
                    var policy = channel.Spec.Policy;
                    var funderId = nodeIds[channel.Spec.From];
                    var problem = withPolicy
                                      ? LndGraph.EdgeProblem(edge, funderId, policy.BaseFeeMsat, policy.FeeRatePpm,
                                                             policy.TimeLockDelta)
                                      : LndGraph.EdgeProblem(edge, funderId);
                    if (problem is not null)
                        missing.Add($"{node.Alias}: {channel} {problem}");
                }

            return missing.Count == 0 ? null : $"{missing.Count} missing, first {missing[0]}";
        }, timeout, $"{channels.Count} channel(s) in every LND's graph", cancellationToken, TimeSpan.FromSeconds(1));

    /// <summary>The set-up steps of <see cref="SetUpAsync(TestTopology, LndRegtestNetworkOptions, CancellationToken)"/>.</summary>
    private sealed class SetUp(TestTopology topology, LndRegtestNetworkOptions options)
    {
        private readonly LndRegtestNetworkSpec _spec = options.Spec.EnsureValid();
        private readonly Dictionary<string, TimeSpan> _timings = new(topology.Timings, StringComparer.Ordinal);
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly TimeSpan _offset = topology.Timings.Values.DefaultIfEmpty(TimeSpan.Zero).Max();

        public async Task<LndRegtestNetwork> RunAsync(bool ownsTopology, CancellationToken cancellationToken)
        {
            var chain = topology.Chain as BitcoinCoreTopologyChain
                     ?? throw new InvalidOperationException("The LND regtest network runs on a BitcoinCoreTopologyChain");
            var nodes = _spec.Nodes.Select(n => topology.Node<LndNode>(n.Alias)).ToList();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                await node.WaitServerActiveAsync(options.ReadyTimeout, cancellationToken).ConfigureAwait(false);
                ids[node.Alias] = await node.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
            }

            Mark("server-active");

            // The miner's reserve: coinbases to its wallet, matured by blocks to the burn address
            if (_spec.MinerReserveBlocks > 0)
            {
                await chain.Chain.MineAsync(_spec.MinerReserveBlocks, cancellationToken).ConfigureAwait(false);
                await chain.Chain.MineAsync(100, cancellationToken, BitcoinCoreTopologyChain.BurnAddress)
                           .ConfigureAwait(false);
                Mark("reserve");
            }

            // Every LND connected to every other one, as permanent peers (LNUnit connects them all): one dial per
            // pair, all at once (LND answers a permanent connect before the connection is up)
            var dials = new List<Task>();
            for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                    dials.Add(ConnectPermanentAsync(nodes[i], new TestPeerAddress(ids[nodes[j].Alias], nodes[j].Alias,
                                                                                  LndWorkload.P2pPort),
                                                    options.StepTimeout, cancellationToken));
            await Task.WhenAll(dials).ConfigureAwait(false);
            Mark("peers");

            // Wallets: WalletUtxoCount outputs each, confirmed
            if (_spec.WalletUtxoCount > 0)
            {
                // One transaction (bitcoind's sendmany) with every node's outputs
                var expected = new Dictionary<string, long>(StringComparer.Ordinal);
                var outputs = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    var before = await node.GetConfirmedBalanceSatAsync(cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < _spec.WalletUtxoCount; i++)
                        outputs[await node.GetNewAddressAsync(cancellationToken).ConfigureAwait(false)] =
                            _spec.WalletUtxoSat;
                    expected[node.Alias] = before + _spec.WalletUtxoCount * _spec.WalletUtxoSat;
                }

                await chain.Chain.Rpc.SendManyAsync(outputs, null, cancellationToken).ConfigureAwait(false);

                await chain.MineAsync(TopologyDeployer.ConfirmationBlocks, cancellationToken).ConfigureAwait(false);
                await TopologyDeployer.WaitAllAtTipAsync(chain, topology.Nodes.Values, options.StepTimeout,
                                                         cancellationToken)
                                      .ConfigureAwait(false);
                foreach (var node in nodes)
                    await Poll.UntilDoneAsync(async ct =>
                    {
                        var balance = await node.GetConfirmedBalanceSatAsync(ct).ConfigureAwait(false);
                        return balance >= expected[node.Alias]
                                   ? null
                                   : $"{balance} of {expected[node.Alias]} sat confirmed";
                    }, options.StepTimeout, $"{node.Alias}'s wallet funded", cancellationToken).ConfigureAwait(false);
                Mark("fundings");
            }

            // The opens: each funder spends one confirmed output per open, all confirmed together
            var opens = new List<(LndRegtestChannelSpec Spec, string TxId, int OutputIndex)>();
            foreach (var channel in _spec.Channels)
            {
                var funder = topology.Node<LndNode>(channel.From);
                var point = await funder.Lightning.OpenChannelSyncAsync(new OpenChannelRequest
                {
                    NodePubkey = ByteString.CopyFrom(Convert.FromHexString(ids[channel.To])),
                    LocalFundingAmount = channel.CapacitySat,
                    PushSat = channel.PushSat,
                    SatPerVbyte = _spec.FundingSatPerVbyte,
                    Private = !_spec.AnnounceChannels
                }, deadline: DateTime.UtcNow.Add(options.StepTimeout), cancellationToken: cancellationToken)
                                       .ResponseAsync.ConfigureAwait(false);
                var txId = LndMapping.TxIdOf(point);
                await chain.Chain.WaitForMempoolAsync(txId, cancellationToken, options.StepTimeout)
                           .ConfigureAwait(false);
                opens.Add((channel, txId, (int)point.OutputIndex));
            }

            Mark("opens");

            var channels = new List<LndRegtestChannel>();
            if (opens.Count > 0)
            {
                await chain.MineAsync(TopologyDeployer.ConfirmationBlocks, cancellationToken).ConfigureAwait(false);
                await TopologyDeployer.WaitAllAtTipAsync(chain, topology.Nodes.Values, options.StepTimeout,
                                                         cancellationToken)
                                      .ConfigureAwait(false);
                foreach (var (spec, txId, outputIndex) in opens)
                {
                    var funder = topology.Node<LndNode>(spec.From);
                    var peer = topology.Node<LndNode>(spec.To);
                    await WaitActiveBothEndsAsync(funder, peer, txId, options.StepTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    channels.Add(new LndRegtestChannel(spec, txId, outputIndex,
                                                       await ChanIdAsync(funder, peer, txId, cancellationToken)
                                                           .ConfigureAwait(false)));
                }

                Mark("active");

                // The funders' policies (LNUnit retries until LND takes it)
                foreach (var channel in channels)
                {
                    var funder = topology.Node<LndNode>(channel.Spec.From);
                    var policy = channel.Spec.Policy;
                    await Poll.UntilDoneAsync(async ct =>
                    {
                        try
                        {
                            var response = await funder.Lightning.UpdateChannelPolicyAsync(new PolicyUpdateRequest
                            {
                                ChanPoint = LndMapping.ToChannelPoint(channel.FundingTxId,
                                                                      (uint)channel.OutputIndex),
                                BaseFeeMsat = policy.BaseFeeMsat,
                                FeeRatePpm = policy.FeeRatePpm,
                                TimeLockDelta = policy.TimeLockDelta
                            }, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
                            return response.FailedUpdates.Count == 0
                                       ? null
                                       : string.Join(", ", response.FailedUpdates.Select(f => f.UpdateError));
                        }
                        catch (RpcException e) when (!ct.IsCancellationRequested)
                        {
                            return $"{e.StatusCode}: {e.Status.Detail}";
                        }
                    }, options.StepTimeout, $"{funder.Alias}'s policy on {channel}", cancellationToken,
                                              TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                Mark("policies");

                if (options.WaitForGraph)
                {
                    await WaitInEveryGraphAsync(nodes, channels, ids, withPolicy: true, options.StepTimeout,
                                                cancellationToken)
                        .ConfigureAwait(false);
                    Mark("graph");
                }
            }

            options.Log?.Invoke($"[lnd-network] {topology.Run.Namespace}: {nodes.Count} LND nodes, {channels.Count} "
                              + $"channels ready at {(_offset + _watch.Elapsed).TotalSeconds:F1} s ("
                              + string.Join(", ", _timings.OrderBy(t => t.Value)
                                                          .Select(t => $"{t.Key} {t.Value.TotalSeconds:F1}"))
                              + ")");
            return new LndRegtestNetwork(topology, options, nodes, channels, _timings, ownsTopology);
        }

        private void Mark(string phase)
        {
            _timings[phase] = _offset + _watch.Elapsed;
            options.Log?.Invoke($"[lnd-network] {topology.Run.Namespace}: {phase} at "
                              + $"{_timings[phase].TotalSeconds:F1} s");
        }
    }
}

/// <summary>A startup (or opened) channel of the network: its declaration, funding outpoint and LND's <c>chan_id</c>.</summary>
public sealed record LndRegtestChannel(LndRegtestChannelSpec Spec, string FundingTxId, int OutputIndex, ulong ChanId)
{
    /// <summary>LND's <c>chan_id</c> as <c>block x tx x output</c>.</summary>
    public string ShortChannelId => LndMapping.FormatShortChannelId(ChanId) ?? "unconfirmed";

    public override string ToString() => $"{Spec.From}->{Spec.To} {ShortChannelId}";
}

/// <summary>What <see cref="LndRegtestNetwork.RestartAsync"/> did.</summary>
/// <param name="ServerActiveAfter">From the restart's start until LND was <c>SERVER_ACTIVE</c> and synced again.</param>
/// <param name="ChannelsActiveAfter">From the restart's start until every awaited channel was active on both ends.</param>
public sealed record LndRestartResult(string Alias, bool Killed, string? PodUidBefore, string? PodUidAfter,
                                      string? PodIpBefore, string PodIpAfter, TimeSpan ServerActiveAfter,
                                      TimeSpan ChannelsActiveAfter, int PeersRedialled, int ChannelsAwaited)
{
    public override string ToString() =>
        $"{Alias} {(Killed ? "killed" : "restarted")}: pod {PodIpBefore} -> {PodIpAfter}, SERVER_ACTIVE after "
      + $"{ServerActiveAfter.TotalSeconds:F1} s, {PeersRedialled} peers redialled, {ChannelsAwaited} channels active "
      + $"again after {ChannelsActiveAfter.TotalSeconds:F1} s";
}

/// <summary>What <see cref="LndRegtestNetwork.PayAlongAsync"/> got: the result, the attempts and the route's channels.</summary>
public sealed record LndRoutedPayment(bool Succeeded, int Attempts, IReadOnlyList<ulong> RouteChanIds,
                                      string? FailureReason, string PaymentHashHex);