namespace NLightning.Testing.Cluster.Tests.Topology.Lnd;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Topology;
using Cluster.Topology.Lnd;

public class LndRegtestNetworkSpecTests
{
    [Fact]
    public void Given_TheDefaultSpec_When_Read_Then_ItIsTheDockerFixturesNetwork()
    {
        // Arrange
        var spec = LndRegtestNetworkSpec.Default;

        // Assert: LightningRegtestNetworkFixture's nodes, alice's flags, channels, pushes and LNUnit's funding
        Assert.Empty(spec.Validate());
        Assert.Equal("miner", spec.ChainName);
        Assert.Equal(["alice", "bob", "carol", "david"], spec.Aliases);
        Assert.Equal(["--protocol.rbf-coop-close", "--accept-keysend"], spec.GetNode("alice").Args);
        Assert.All(spec.Nodes.Skip(1), n => Assert.Empty(n.Args));
        Assert.Equal([
                         ("alice", "bob", 10_000_000L, 0L), ("bob", "alice", 10_000_000L, 1_000_000L),
                         ("carol", "alice", 10_000_000L, 1_000_000L), ("carol", "bob", 10_000_000L, 1_000_000L)
                     ],
                     spec.Channels.Select(c => (c.From, c.To, c.CapacitySat, c.PushSat)));
        Assert.All(spec.Channels, c => Assert.Equal(new LndChannelPolicy(0, 0, 40), c.Policy));
        Assert.Equal(4_269_000_000, spec.WalletUtxoSat);
        Assert.Equal(2, spec.WalletUtxoCount);
        Assert.Equal(10UL, spec.FundingSatPerVbyte);
        Assert.True(spec.AnnounceChannels);
        Assert.Throws<KeyNotFoundException>(() => spec.GetNode("erin"));
    }

    [Fact]
    public void Given_ABrokenSpec_When_Validated_Then_EveryProblemIsListed()
    {
        // Arrange
        var spec = new LndRegtestNetworkSpec(
        [
            new LndRegtestNodeSpec("alice"),
            new LndRegtestNodeSpec("alice"),
            new LndRegtestNodeSpec("Bob_1"),
            new LndRegtestNodeSpec("miner")
        ],
        [
            new LndRegtestChannelSpec("alice", "erin", 1_000_000),
            new LndRegtestChannelSpec("alice", "alice", 1_000_000),
            new LndRegtestChannelSpec("alice", "miner", 1_000_000, 1_000_000),
            new LndRegtestChannelSpec("miner", "alice", 0),
            new LndRegtestChannelSpec("miner", "alice", 20_000_000)
        ], WalletUtxoSat: 10_000_000, WalletUtxoCount: 2, MinerReserveBlocks: -1);

        // Act
        var errors = spec.Validate();

        // Assert
        Assert.Contains(errors, e => e.Contains("'alice': declared twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'Bob_1': not a DNS-1123 label", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'miner': the chain node's name", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'erin' is not a node of the network", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cannot open a channel to itself", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("push 1000000 sat is outside", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("capacity 0 sat is not positive", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("does not fit one wallet output", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'alice' funds 3 channels but gets only 2", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("miner reserve", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => spec.EnsureValid());
    }

    [Fact]
    public void Given_TheDefaultOptions_When_Declared_Then_TheTopologyIsTheChainAndTheLndNodesOnPvcs()
    {
        // Arrange
        var builder = new TopologyBuilder();
        var options = new LndRegtestNetworkOptions
        {
            ReadyTimeout = TimeSpan.FromMinutes(5),
            StepTimeout = TimeSpan.FromMinutes(2),
            ConfigureBuilder = b => b.AddCln("extra")
        };

        // Act
        LndRegtestNetwork.Declare(builder, options);
        var spec = builder.Build();

        // Assert: no fundings or channels in the topology (the set-up does them the LND way)
        Assert.Equal(["miner", "alice", "bob", "carol", "david", "extra"], spec.Nodes.Select(n => n.Name));
        Assert.Equal(NodeKind.BitcoinCore, spec.ChainNode.Kind);
        Assert.All(spec.Nodes.Skip(1).Take(4), n => Assert.Equal(NodeKind.Lnd, n.Kind));
        Assert.All(spec.Nodes, n => Assert.Equal(NodeStorage.Persistent, n.Storage));
        Assert.Equal(["--protocol.rbf-coop-close", "--accept-keysend"], spec.GetNode("alice").Args);
        Assert.Empty(spec.Fundings);
        Assert.Empty(spec.Channels);
        Assert.Equal(TimeSpan.FromMinutes(5), builder.ReadyTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), builder.StepTimeout);
        Assert.Null(spec.GetNode("alice").Image);
    }

    [Fact]
    public void Given_AnImageAndEphemeralStorage_When_Declared_Then_TheLndNodesUseThem()
    {
        // Arrange
        var builder = new TopologyBuilder();
        var image = ImageVersions.Lnd with { Repository = "nltg-spike-lnd" };

        // Act
        LndRegtestNetwork.Declare(builder, new LndRegtestNetworkOptions
        {
            LndImage = image,
            Storage = NodeStorage.Ephemeral
        });
        var spec = builder.Build();

        // Assert
        Assert.All(spec.LightningNodes, n => Assert.Equal(image, n.Image));
        Assert.All(spec.Nodes, n => Assert.Equal(NodeStorage.Ephemeral, n.Storage));
    }

    [Fact]
    public void Given_AnInvalidSpec_When_Declared_Then_NothingIsDeclared()
    {
        // Arrange
        var builder = new TopologyBuilder();
        var options = new LndRegtestNetworkOptions
        {
            Spec = LndRegtestNetworkSpec.Default with { WalletUtxoCount = 1 }
        };

        // Act & Assert: carol opens two channels from one output
        var error = Assert.Throws<ArgumentException>(() => LndRegtestNetwork.Declare(builder, options));
        Assert.Contains("'carol' funds 2 channels", error.Message, StringComparison.Ordinal);
    }
}