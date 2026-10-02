namespace NLightning.Testing.Cluster.Topology;

using Images;
using Nodes;
using Nodes.Cln;
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
        [NodeKind.Cln] = new ClnNodeDeployer()
    };

    private ChainFactory _chainFactory = TopologyBitcoind.DeployAsync;

    /// <summary>Deploys the chain node of a topology and returns it ready, with a mature wallet.</summary>
    public delegate Task<ITopologyChain> ChainFactory(TestRun run, TopologyNodeSpec chainNode,
                                                      TimeSpan readyTimeout, CancellationToken cancellationToken);

    /// <summary>How long each node may take to become ready.</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long the funding, the opens and the waits for the tip may take, each.</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Where the deployment writes its progress (with timings); null for nowhere.</summary>
    public Action<string>? Log { get; set; }

    public TopologyBuilder AddNode(string name, NodeKind kind, ImageRef? image = null,
                                   IReadOnlyList<string>? extraArgs = null)
    {
        _nodes.Add(new TopologyNodeSpec(name, kind, image, extraArgs));
        return this;
    }

    /// <summary>The chain node (regtest bitcoind).</summary>
    public TopologyBuilder AddBitcoinCore(string name, ImageRef? image = null,
                                          IReadOnlyList<string>? extraArgs = null) =>
        AddNode(name, NodeKind.BitcoinCore, image, extraArgs);

    /// <summary>A Core Lightning node (<see cref="ClnNode"/>).</summary>
    public TopologyBuilder AddCln(string name, ImageRef? image = null, IReadOnlyList<string>? extraArgs = null) =>
        AddNode(name, NodeKind.Cln, image, extraArgs);

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

    /// <summary>Replaces the chain backend (<see cref="TopologyBitcoind"/> by default).</summary>
    public TopologyBuilder UseChain(ChainFactory chainFactory)
    {
        _chainFactory = chainFactory ?? throw new ArgumentNullException(nameof(chainFactory));
        return this;
    }

    /// <summary>The validated spec.</summary>
    public TopologySpec Build()
    {
        var spec = new TopologySpec([.. _nodes], [.. _fundings], [.. _channels]).EnsureValid();
        var missing = spec.LightningNodes.Select(n => n.Kind).Distinct().Where(k => !_deployers.ContainsKey(k))
                          .ToList();
        return missing.Count == 0
                   ? spec
                   : throw new ArgumentException($"No deployer for {string.Join(", ", missing)}; register one with "
                                               + $"{nameof(UseDeployer)}");
    }

    /// <summary>Deploys the topology into <paramref name="run"/>: nodes, fundings, channels (all active).</summary>
    public Task<TestTopology> BuildAsync(TestRun run, CancellationToken cancellationToken) =>
        new TopologyDeployer(Build(), _deployers, _chainFactory, ReadyTimeout, StepTimeout, Log)
           .DeployAsync(run, cancellationToken);
}