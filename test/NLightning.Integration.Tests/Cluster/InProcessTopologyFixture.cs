namespace NLightning.Integration.Tests.Cluster;

using Testing.Cluster.Topology;

/// <summary>
/// A warm topology (<see cref="ClusterTopologyFixture"/>: built once per xunit collection, nothing reset between tests)
/// with in-process NLightning nodes: the fixture owns the <see cref="InProcessNodeDeployer"/>, registers it for the
/// topology's <see cref="Testing.Cluster.Nodes.NodeKind.NLightning"/> nodes and stops those nodes after the topology and
/// before the namespace is deleted. Every isolation rule of <see cref="ClusterTopologyFixture"/> applies, and the
/// in-process nodes share them: their wallets, channels and payments carry over from test to test.
/// </summary>
/// <example>
/// <code>
/// public sealed class ClnClusterFixture : InProcessTopologyFixture
/// {
///     protected override string Suite => "nltg-cln";
///     protected override void ConfigureTopology(TopologyBuilder builder) =>
///         builder.AddBitcoinCore("miner").AddNLightning("nltg").AddCln("cln").FundWallet("nltg", 2_000_000);
/// }
/// </code>
/// </example>
public abstract class InProcessTopologyFixture : ClusterTopologyFixture
{
    private InProcessNodeDeployer? _deployer;

    /// <summary>The deployer of the fixture's in-process nodes (created on first use by <see cref="CreateDeployer"/>).</summary>
    public InProcessNodeDeployer Deployer => _deployer ??= CreateDeployer();

    /// <summary>The in-process node named <paramref name="name"/>.</summary>
    public InProcessNode InProcessNode(string name) => Topology.InProcessNode(name);

    /// <summary>Declares the topology; the in-process deployer is registered after it.</summary>
    protected abstract void ConfigureTopology(TopologyBuilder builder);

    /// <summary>The deployer, with any node options, settings or database the suite needs.</summary>
    protected virtual InProcessNodeDeployer CreateDeployer() => new();

    protected sealed override void Configure(TopologyBuilder builder)
    {
        ConfigureTopology(builder);
        builder.UseInProcessNodes(Deployer);
    }

    protected override async ValueTask OnStoppingAsync()
    {
        if (_deployer is not null)
            await _deployer.DisposeAsync();
    }
}