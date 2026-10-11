namespace NLightning.Integration.Tests.Cluster;

using Testing.Cluster.Nodes;
using Testing.Cluster.Topology;

/// <summary>The warm in-process fixture without a cluster: what it declares and that it stops cleanly unstarted.</summary>
public class InProcessTopologyFixtureTests
{
    private sealed class Fixture : InProcessTopologyFixture
    {
        protected override string Suite => "unit-inprocess";

        protected override void ConfigureTopology(TopologyBuilder builder) =>
            builder.AddBitcoinCore("miner").AddNLightning("nltg").AddCln("cln").FundWallet("nltg", 2_000_000);

        public TopologyBuilder Declare()
        {
            var builder = new TopologyBuilder();
            Configure(builder);
            return builder;
        }
    }

    [Fact]
    public void Given_AnInProcessFixture_When_ItsTopologyIsDeclared_Then_ItsDeployerServesTheNLightningNodes()
    {
        // Arrange
        var fixture = new Fixture();

        // Act
        var spec = fixture.Declare().Build();

        // Assert (Build refuses a Lightning node kind without a deployer)
        Assert.Contains(spec.LightningNodes, n => n is { Name: "nltg", Kind: NodeKind.NLightning });
        Assert.Equal(NodeKind.NLightning, fixture.Deployer.Kind);
        Assert.Empty(fixture.Deployer.Nodes);
    }

    [Fact]
    public async Task Given_AnInProcessFixtureNeverStarted_When_Disposed_Then_ItStopsWithoutNodes()
    {
        // Arrange
        var fixture = new Fixture();
        _ = fixture.Deployer;

        // Act + Assert (twice is fine)
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
        Assert.Empty(fixture.Deployer.Nodes);
    }
}