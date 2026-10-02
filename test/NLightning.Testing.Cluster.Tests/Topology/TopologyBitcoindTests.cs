namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Topology;

public class TopologyBitcoindTests
{
    [Fact]
    public void Given_AChainNode_When_ItsWorkloadIsBuilt_Then_ItRunsRegtestBitcoindWithItsDataOnAPvc()
    {
        // Act
        var workload = TopologyBitcoind.Workload("miner");

        // Assert
        Assert.Equal(NodeKind.BitcoinCore, workload.Kind);
        Assert.Equal(ImageVersions.BitcoinCore, workload.Image);
        Assert.Equal("/home/bitcoin/.bitcoin", workload.Data!.MountPath);
        Assert.Equal(["rpc", "p2p", "zmq-block", "zmq-tx"], workload.Ports.Select(p => p.Name));
        Assert.Equal(["bitcoin-cli", "-regtest", "-rpcuser=nltg", "-rpcpassword=nltg", "-rpcport=18443",
                      "getblockchaininfo"], workload.ReadinessProbe!.Exec.Command);
        Assert.Equal("bitcoind", workload.Args[0]);
        Assert.Contains("-regtest", workload.Args);
        Assert.Contains("-fallbackfee=0.0002", workload.Args);
        Assert.Contains("-zmqpubrawblock=tcp://0.0.0.0:28334", workload.Args);
        Assert.Contains("-rpcallowip=0.0.0.0/0", workload.Args);
    }

    [Fact]
    public void Given_AnImageAndExtraArgs_When_TheWorkloadIsBuilt_Then_TheyAreUsed()
    {
        // Act
        var workload = TopologyBitcoind.Workload("miner", ImageVersions.BitcoinCore31, ["-blockfilterindex=1"]);

        // Assert
        Assert.Equal(ImageVersions.BitcoinCore31, workload.Image);
        Assert.Equal("-blockfilterindex=1", workload.Args[^1]);
    }

    [Fact]
    public void Given_AWallet_When_ACliCommandIsBuilt_Then_ItTargetsThatWallet()
    {
        // Act
        var command = TopologyBitcoind.CliCommand("miner", ["getnewaddress"]);

        // Assert
        Assert.Equal(["bitcoin-cli", "-regtest", "-rpcuser=nltg", "-rpcpassword=nltg", "-rpcport=18443",
                      "-rpcwallet=miner", "getnewaddress"], command);
    }

    [Theory]
    [InlineData(1, "0.00000001")]
    [InlineData(2_000_000, "0.02000000")]
    [InlineData(100_000_000, "1.00000000")]
    [InlineData(123_456_789_012, "1234.56789012")]
    public void Given_Satoshis_When_FormattedAsBtc_Then_TheyHaveEightDecimals(long sat, string expected)
    {
        // Act + Assert
        Assert.Equal(expected, TopologyBitcoind.FormatBtc(sat));
    }

    [Fact]
    public void Given_NoAmount_When_FormattedAsBtc_Then_ItIsRefused()
    {
        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyBitcoind.FormatBtc(0));
    }

    [Fact]
    public async Task Given_ANode_When_TheChainMines_Then_ItGeneratesToANewMinerAddress()
    {
        // Arrange
        var node = new FakeNodeHandle("miner", NodeKind.BitcoinCore)
        {
            Respond = c => c[^1] == "getnewaddress"
                               ? FakeNodeHandle.Ok("bcrt1qminer\n")
                               : FakeNodeHandle.Ok("[\"h1\",\"h2\"]")
        };
        var chain = new TopologyBitcoind(node);

        // Act
        var hashes = await chain.MineAsync(2, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["h1", "h2"], hashes);
        Assert.Equal(["-rpcwallet=miner", "generatetoaddress", "2", "bcrt1qminer"], node.Commands[1].Skip(5));
        Assert.Equal("miner", chain.RpcHost);
    }

    [Fact]
    public async Task Given_BitcoinCliFails_When_Called_Then_TheErrorCarriesStdErr()
    {
        // Arrange
        var node = new FakeNodeHandle("miner", NodeKind.BitcoinCore)
        {
            Respond = _ => FakeNodeHandle.Fail(28, string.Empty, "error code: -28 Loading wallet")
        };

        // Act
        var exception = await Assert.ThrowsAsync<KubeExecException>(
                            () => new TopologyBitcoind(node).GetBlockCountAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("Loading wallet", exception.Message);
    }
}