namespace NLightning.Testing.Cluster.Tests.Nodes.Lnd;

using Cluster.Images;
using Cluster.Nodes;
using Cluster.Nodes.Lnd;
using Cluster.Topology;
using Topology;

public class LndNodeDeployerTests
{
    [Fact]
    public void Given_AChainAndANodeSpec_When_TheOptionsAreBuilt_Then_LndFollowsTheChainNodesRpcAndZmq()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore));
        var spec = new TopologyNodeSpec("alice", NodeKind.Lnd, ExtraArgs: ["--protocol.rbf-coop-close"]);

        // Act
        var options = LndNodeDeployer.BuildOptions(chain, spec);
        var args = LndWorkload.BuildArgs(options);

        // Assert
        Assert.Equal("alice", options.Alias);
        Assert.Equal(ImageVersions.Lnd, options.Image);
        Assert.Equal("miner", options.BitcoindHost);
        Assert.Equal("user", options.BitcoindRpcUser);
        Assert.Equal("secret", options.BitcoindRpcPassword);
        Assert.Contains("--bitcoind.rpchost=miner:18443", args);
        Assert.Contains("--bitcoind.zmqpubrawblock=tcp://miner:28332", args);
        Assert.Contains("--bitcoind.zmqpubrawtx=tcp://miner:28333", args);
        Assert.Equal("--protocol.rbf-coop-close", args[^1]);
    }

    [Fact]
    public void Given_NoChain_When_DefaultOptionsAreUsed_Then_TheyMatchTheSharedBitcoind()
    {
        // Act
        var options = new LndNodeOptions("bob");

        // Assert
        Assert.Equal("miner", options.BitcoindHost);
        Assert.Equal(Cluster.Nodes.BitcoinCore.BitcoinCorePorts.Rpc, options.BitcoindRpcPort);
        Assert.Equal(new Cluster.Nodes.BitcoinCore.BitcoinCoreOptions().RpcUser, options.BitcoindRpcUser);
        Assert.Equal(new Cluster.Nodes.BitcoinCore.BitcoinCoreOptions().RpcPassword, options.BitcoindRpcPassword);
        Assert.Equal(Cluster.Nodes.BitcoinCore.BitcoinCorePorts.ZmqRawBlock, options.ZmqRawBlockPort);
        Assert.Equal(Cluster.Nodes.BitcoinCore.BitcoinCorePorts.ZmqRawTx, options.ZmqRawTxPort);
    }

    [Fact]
    public void Given_AMixedTopology_When_Built_Then_LndAndClnHaveDeployersByDefault()
    {
        // Act
        var spec = new TopologyBuilder().AddBitcoinCore("miner").AddLnd("alice").AddCln("bob")
                                        .FundWallet("alice", 2_000_000).AddChannel("alice", "bob", 1_000_000)
                                        .Build();

        // Assert
        Assert.Equal(NodeKind.Lnd, new LndNodeDeployer().Kind);
        Assert.Equal([NodeKind.Lnd, NodeKind.Cln], spec.LightningNodes.Select(n => n.Kind));
    }
}