namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Nodes;
using Cluster.Topology;

public class TopologySpecTests
{
    private static TopologyBuilder ClnPair() =>
        new TopologyBuilder().AddBitcoinCore("miner").AddCln("alice").AddCln("bob").FundWallet("alice", 2_000_000)
                             .AddChannel("alice", "bob", 1_000_000, 100_000_000);

    [Fact]
    public void Given_AClnPair_When_Built_Then_TheSpecHasTheChainTheNodesAndTheChannel()
    {
        // Act
        var spec = ClnPair().Build();

        // Assert
        Assert.Equal("miner", spec.ChainNode.Name);
        Assert.Equal(["alice", "bob"], spec.LightningNodes.Select(n => n.Name));
        Assert.Equal(new TopologyChannelSpec("alice", "bob", 1_000_000, 100_000_000), Assert.Single(spec.Channels));
        Assert.Equal(new TopologyFundingSpec("alice", 2_000_000), Assert.Single(spec.Fundings));
        Assert.Empty(spec.Validate());
        Assert.Equal(NodeKind.Cln, spec.GetNode("bob").Kind);
        Assert.Throws<KeyNotFoundException>(() => spec.GetNode("carol"));
    }

    [Fact]
    public void Given_NoChainNode_When_Validated_Then_ItIsReported()
    {
        // Act
        var errors = new TopologySpec([new TopologyNodeSpec("alice", NodeKind.Cln)], [], []).Validate();

        // Assert
        Assert.Contains("a topology needs exactly one BitcoinCore node, it has 0", errors);
    }

    [Fact]
    public void Given_BadNames_When_Validated_Then_EachIsReported()
    {
        // Arrange
        var spec = new TopologySpec(
        [
            new TopologyNodeSpec("miner", NodeKind.BitcoinCore), new TopologyNodeSpec("Alice", NodeKind.Cln),
            new TopologyNodeSpec("bob", NodeKind.Cln), new TopologyNodeSpec("bob", NodeKind.Cln),
            new TopologyNodeSpec("pg", NodeKind.Postgres)
        ], [], []);

        // Act
        var errors = spec.Validate();

        // Assert
        Assert.Contains(errors, e => e.StartsWith("node 'Alice': not a DNS-1123 label", StringComparison.Ordinal));
        Assert.Contains("node 'bob': declared twice", errors);
        Assert.Contains("node 'pg': kind Postgres is not a chain or Lightning node", errors);
    }

    [Fact]
    public void Given_BadFundingsAndChannels_When_Validated_Then_EachIsReported()
    {
        // Arrange
        var spec = new TopologySpec(
            [new TopologyNodeSpec("miner", NodeKind.BitcoinCore), new TopologyNodeSpec("alice", NodeKind.Cln)],
            [new TopologyFundingSpec("miner", 1_000), new TopologyFundingSpec("alice", 0)],
            [
                new TopologyChannelSpec("alice", "alice", 100), new TopologyChannelSpec("alice", "carol", 0),
                new TopologyChannelSpec("alice", "miner", 100, 100_001)
            ]);

        // Act
        var errors = spec.Validate();

        // Assert
        Assert.Contains("funding of 'miner': not a Lightning node of the topology", errors);
        Assert.Contains("funding of 'alice': amount 0 sat is not positive", errors);
        Assert.Contains("channel alice -> alice: a node cannot open a channel to itself", errors);
        Assert.Contains("channel alice -> carol: 'carol' is not a Lightning node of the topology", errors);
        Assert.Contains("channel alice -> carol: capacity 0 sat is not positive", errors);
        Assert.Contains("channel alice -> miner: 'miner' is not a Lightning node of the topology", errors);
        Assert.Contains("channel alice -> miner: push 100001 msat is outside 0..capacity", errors);
    }

    [Fact]
    public void Given_AFunderWithoutEnoughFunds_When_Validated_Then_ItIsReported()
    {
        // Arrange
        var builder = new TopologyBuilder().AddBitcoinCore("miner").AddCln("alice").AddCln("bob")
                                           .FundWallet("alice", 600_000).FundWallet("alice", 400_000)
                                           .AddChannel("alice", "bob", 1_000_000);

        // Act
        var exception = Assert.Throws<ArgumentException>(() => builder.Build());

        // Assert
        Assert.Contains("node 'alice' opens 1000000 sat of channels but its wallet is funded with only 1000000 sat",
                        exception.Message);
    }

    [Fact]
    public void Given_AKindWithoutADeployer_When_Built_Then_TheBuilderAsksForOne()
    {
        // Arrange
        var builder = new TopologyBuilder().AddBitcoinCore("miner").AddNode("carol", NodeKind.Lnd);

        // Act
        var exception = Assert.Throws<ArgumentException>(() => builder.Build());

        // Assert
        Assert.Contains("No deployer for Lnd", exception.Message);
    }

    [Fact]
    public void Given_ADeployerForAKind_When_Registered_Then_TheSpecBuilds()
    {
        // Arrange
        var builder = new TopologyBuilder().AddBitcoinCore("miner").AddNode("carol", NodeKind.Lnd)
                                           .UseDeployer(new NoDeployer(NodeKind.Lnd));

        // Act
        var spec = builder.Build();

        // Assert
        Assert.Equal(NodeKind.Lnd, Assert.Single(spec.LightningNodes).Kind);
    }

    [Theory]
    [InlineData(NodeKind.Lnd, true)]
    [InlineData(NodeKind.Cln, true)]
    [InlineData(NodeKind.Eclair, true)]
    [InlineData(NodeKind.Ldk, true)]
    [InlineData(NodeKind.NLightning, true)]
    [InlineData(NodeKind.BitcoinCore, false)]
    [InlineData(NodeKind.Tor, false)]
    public void Given_AKind_When_Asked_Then_OnlyImplementationsAreLightning(NodeKind kind, bool expected)
    {
        // Act + Assert
        Assert.Equal(expected, TopologySpec.IsLightning(kind));
    }

    private sealed class NoDeployer(NodeKind kind) : ILightningNodeDeployer
    {
        public NodeKind Kind => kind;

        public Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                                        CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}