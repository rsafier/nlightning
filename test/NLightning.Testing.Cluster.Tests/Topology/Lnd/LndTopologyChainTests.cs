namespace NLightning.Testing.Cluster.Tests.Topology.Lnd;

using Cluster.Images;
using Cluster.Nodes;
using Cluster.Nodes.Lnd;
using Cluster.Topology.Lnd;

public class LndTopologyChainTests
{
    [Fact]
    public void Given_TheChain_When_ItsWorkloadIsBuilt_Then_ItIsPolarsBitcoindNamedMinerWithItsDataOnAPvc()
    {
        // Act
        var workload = LndTopologyChain.BuildWorkload();

        // Assert
        Assert.Equal("miner", workload.Name);
        Assert.Equal(NodeKind.BitcoinCore, workload.Kind);
        Assert.Equal(ImageVersions.BitcoinCore, workload.Image);
        Assert.Equal("/home/bitcoin/.bitcoin", workload.Data?.MountPath);
        Assert.Equal(["rpc", "p2p", "zmq-block", "zmq-tx"], workload.Ports.Select(p => p.Name));
        Assert.Equal([.. LndTopologyChain.CliPrefix, "getblockchaininfo"], workload.ReadinessProbe?.Exec.Command);
    }

    [Fact]
    public void Given_TheChainArgs_When_Built_Then_TheyMatchWhatTheLndNodesExpect()
    {
        // Arrange
        var lnd = new LndNodeOptions("alice");

        // Act
        var args = LndTopologyChain.BuildArgs();

        // Assert
        Assert.Equal("bitcoind", args[0]);
        Assert.Contains("-regtest=1", args);
        Assert.Contains($"-rpcuser={lnd.BitcoindRpcUser}", args);
        Assert.Contains($"-rpcpassword={lnd.BitcoindRpcPassword}", args);
        Assert.Contains($"-rpcport={lnd.BitcoindRpcPort}", args);
        Assert.Contains($"-zmqpubrawblock=tcp://0.0.0.0:{lnd.ZmqRawBlockPort}", args);
        Assert.Contains($"-zmqpubrawtx=tcp://0.0.0.0:{lnd.ZmqRawTxPort}", args);
        Assert.Contains("-txindex=1", args);
        Assert.Equal(LndTopologyChain.DefaultName, lnd.BitcoindHost);
    }

    [Fact]
    public void Given_TheCli_When_ItsPrefixIsRead_Then_ItUsesRegtestRpcCredentials()
    {
        // Assert
        Assert.Equal(["bitcoin-cli", "-regtest", "-rpcuser=bitcoin", "-rpcpassword=bitcoin", "-rpcport=18443"],
                     LndTopologyChain.CliPrefix);
    }
}