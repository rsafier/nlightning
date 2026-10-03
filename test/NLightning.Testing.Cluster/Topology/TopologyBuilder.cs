namespace NLightning.Testing.Cluster.Topology;

using Images;
using Kube;
using Nodes;
using Nodes.Cln;
using Nodes.Eclair;
using Nodes.Lnd;
using Run;

/// <summary>
/// The fluent way to declare a <see cref="TopologySpec"/> and deploy it.
/// </summary>
/// <example>
/// <code>
/// await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("cln-pair"), ct);
/// var topology = await new TopologyBuilder()
///     .AddBitcoinCore("miner")
///     .AddCln("alice").AddCln("bob")
///     .FundWallet("alice", 2_000_000)
///     .AddChannel("alice", "bob", 1_000_000)
///     .BuildAsync(run, ct);
/// var invoice = await topology.Node("bob").CreateInvoiceAsync(50_000, "test", ct);
/// </code>
/// </example>
public sealed class TopologyBuilder
{
    private readonly List<TopologyNodeSpec> _nodes = [];
    private readonly List<TopologyFundingSpec> _fundings = [];
    private readonly List<TopologyChannelSpec> _channels = [];
    private readonly Dictionary<NodeKind, ILightningNodeDeployer> _deployers = new()
    {
        [NodeKind.Cln] = new ClnNodeDeployer(),
        [NodeKind.Lnd] = new LndNodeDeployer(),
        [NodeKind.Eclair] = new EclairNodeDeployer()
    };

    private ChainFactory _chainFactory = BitcoinCoreTopologyChain.DeployAsync;
    private ChainEndpointFactory? _chainEndpointFactory = BitcoinCoreTopologyChain.EndpointFor;

    /// <summary>Deploys the chain node of a topology and returns it ready, with a mature wallet.</summary>
    public delegate Task<ITopologyChain> ChainFactory(TestRun run, TopologyNodeSpec chainNode,
                                                      TimeSpan readyTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// The endpoint of the chain node <see cref="ChainFactory"/> will deploy for <c>chainNode</c>, known before it is
    /// up (so the Lightning nodes can start with it).
    /// </summary>
    public delegate ITopologyChainEndpoint ChainEndpointFactory(TopologyNodeSpec chainNode);

    /// <summary>How long each node may take to become ready.</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long the funding, the opens and the waits for the tip may take, each.</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Where the deployment writes its progress (with timings); null for nowhere.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Where nodes without their own storage keep their data: null (the default) takes
    /// <see cref="NodeStorageEnvironment.Variable"/> (<c>NLTG_NODE_STORAGE</c>), else
    /// <see cref="NodeStorage.Persistent"/>. <see cref="NodeStorage.Ephemeral"/> skips the PVC wait (about 6 s per
    /// wave on OrbStack) for topologies whose nodes are never restarted or killed.
    /// </summary>
    public NodeStorage? Storage { get; set; }

    /// <summary>
    /// Start the Lightning nodes whose deployer allows it (<see cref="ILightningNodeDeployer.DeploysWithChain"/>) in
    /// the same wave as the chain node instead of after it (default true; needs the chain's endpoint, see
    /// <see cref="UseChain(ChainFactory, ChainEndpointFactory?)"/>).
    /// </summary>
    public bool DeployNodesWithChain { get; set; } = true;

    /// <summary>
    /// With <see cref="DeployNodesWithChain"/>, how long the Lightning nodes' wave waits for the chain node's name to
    /// resolve (its Service has an address) before it starts, so no node looks the name up too early (CoreDNS caches the
    /// miss for 5 s, <see cref="KubernetesHelper.WaitForServiceAddressAsync"/>). Null (the default) waits up to
    /// <see cref="TopologyDeployer.DefaultChainAddressWait"/> when the chain node is on
    /// <see cref="NodeStorage.Ephemeral"/> storage (its pod has an IP about 1 s after it is created) and not at all on
    /// a PVC (its pod has an IP only after the claim is provisioned, 6-15 s, and the nodes' own claims are better
    /// provisioned meanwhile).
    /// </summary>
    public TimeSpan? ChainAddressWait { get; set; }

    /// <param name="storage">This node's storage; null takes <see cref="Storage"/>.</param>
    public TopologyBuilder AddNode(string name, NodeKind kind, ImageRef? image = null,
                                   IReadOnlyList<string>? extraArgs = null, NodeStorage? storage = null)
    {
        _nodes.Add(new TopologyNodeSpec(name, kind, image, extraArgs, storage));
        return this;
    }

    /// <summary>The chain node (regtest bitcoind).</summary>
    public TopologyBuilder AddBitcoinCore(string name, ImageRef? image = null,
                                          IReadOnlyList<string>? extraArgs = null, NodeStorage? storage = null) =>
        AddNode(name, NodeKind.BitcoinCore, image, extraArgs, storage);

    /// <summary>A Core Lightning node (<see cref="ClnNode"/>).</summary>
    public TopologyBuilder AddCln(string name, ImageRef? image = null, IReadOnlyList<string>? extraArgs = null,
                                  NodeStorage? storage = null) =>
        AddNode(name, NodeKind.Cln, image, extraArgs, storage);

    /// <summary>An LND node (<see cref="LndNode"/>).</summary>
    public TopologyBuilder AddLnd(string name, ImageRef? image = null, IReadOnlyList<string>? extraArgs = null,
                                  NodeStorage? storage = null) =>
        AddNode(name, NodeKind.Lnd, image, extraArgs, storage);

    /// <summary>
    /// An Eclair node (<see cref="EclairNode"/>; its <paramref name="extraConfig"/> are more <c>eclair.conf</c> lines).
    /// Eclair 0.14.3 needs the chain node on <see cref="ImageVersions.BitcoinCore31"/>.
    /// </summary>
    public TopologyBuilder AddEclair(string name, ImageRef? image = null, IReadOnlyList<string>? extraConfig = null,
                                     NodeStorage? storage = null) =>
        AddNode(name, NodeKind.Eclair, image, extraConfig, storage);

    /// <summary>Sends <paramref name="amountSat"/> to <paramref name="node"/>'s wallet (confirmed before the opens).</summary>
    public TopologyBuilder FundWallet(string node, long amountSat)
    {
        _fundings.Add(new TopologyFundingSpec(node, amountSat));
        return this;
    }

    /// <summary>A channel <paramref name="from"/> opens to <paramref name="to"/>, active when the build returns.</summary>
    public TopologyBuilder AddChannel(string from, string to, long capacitySat, long pushMsat = 0,
                                      bool announce = false)
    {
        _channels.Add(new TopologyChannelSpec(from, to, capacitySat, pushMsat, announce));
        return this;
    }

    /// <summary>Registers (or replaces) the deployer of a Lightning implementation.</summary>
    public TopologyBuilder UseDeployer(ILightningNodeDeployer deployer)
    {
        ArgumentNullException.ThrowIfNull(deployer);
        _deployers[deployer.Kind] = deployer;
        return this;
    }

    /// <summary>
    /// Replaces the chain backend (<see cref="BitcoinCoreTopologyChain"/> by default). Without
    /// <paramref name="endpointFactory"/> the Lightning nodes are deployed only once the chain is ready.
    /// </summary>
    public TopologyBuilder UseChain(ChainFactory chainFactory, ChainEndpointFactory? endpointFactory = null)
    {
        _chainFactory = chainFactory ?? throw new ArgumentNullException(nameof(chainFactory));
        _chainEndpointFactory = endpointFactory;
        return this;
    }

    /// <summary>The validated spec, every node's storage resolved (<see cref="Storage"/>).</summary>
    public TopologySpec Build()
    {
        var storage = Storage ?? NodeStorageEnvironment.Read() ?? NodeStorage.Persistent;
        var nodes = _nodes.Select(n => n.Storage is null ? n with { Storage = storage } : n).ToList();
        var spec = new TopologySpec(nodes, [.. _fundings], [.. _channels]).EnsureValid();
        var missing = spec.LightningNodes.Select(n => n.Kind).Distinct().Where(k => !_deployers.ContainsKey(k))
                          .ToList();
        return missing.Count == 0
                   ? spec
                   : throw new ArgumentException($"No deployer for {string.Join(", ", missing)}; register one with "
                                               + $"{nameof(UseDeployer)}");
    }

    /// <summary>
    /// Deploys the topology into <paramref name="run"/>: nodes, fundings, channels (all active). A failed build is
    /// recorded on the run and its diagnostics dumped (<see cref="Diagnostics.ClusterDiagnostics"/>) before it throws.
    /// </summary>
    public Task<TestTopology> BuildAsync(TestRun run, CancellationToken cancellationToken)
    {
        var deployer = new TopologyDeployer(Build(), _deployers, _chainFactory, ReadyTimeout, StepTimeout, Log,
                                            DeployNodesWithChain ? _chainEndpointFactory : null)
        {
            ChainAddressWait = ChainAddressWait
        };
        return Diagnostics.ClusterDiagnostics.CaptureOnFailureAsync(
            run, "topology build", () => deployer.DeployAsync(run, cancellationToken));
    }
}