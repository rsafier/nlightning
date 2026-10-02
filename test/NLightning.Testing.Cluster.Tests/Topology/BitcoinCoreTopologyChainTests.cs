using k8s;

namespace NLightning.Testing.Cluster.Tests.Topology;

using Chain;
using Cluster.Chain;
using Cluster.Images;
using Cluster.Nodes;
using Cluster.Nodes.BitcoinCore;
using Cluster.Topology;

public class BitcoinCoreTopologyChainTests
{
    [Fact]
    public void Given_AChainNodeSpec_When_TheOptionsAreBuilt_Then_TheNameImageAndFlagsAreUsed()
    {
        // Arrange
        var spec = new TopologyNodeSpec("chain", NodeKind.BitcoinCore, ImageVersions.BitcoinCore31,
                                        ["-blockfilterindex=1"]);

        // Act
        var options = BitcoinCoreTopologyChain.OptionsFor(spec);

        // Assert
        Assert.Equal("chain", options.Name);
        Assert.Equal(ImageVersions.BitcoinCore31, options.Image);
        Assert.Equal(["-blockfilterindex=1"], options.ExtraArgs);
        Assert.Equal("miner", options.Wallet);
    }

    [Fact]
    public void Given_NoImageOverride_When_TheOptionsAreBuilt_Then_TheVersionTablesBitcoindIsUsed()
    {
        // Act
        var options = BitcoinCoreTopologyChain.OptionsFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore));

        // Assert
        Assert.Equal(ImageVersions.BitcoinCore, options.Image);
        Assert.Empty(options.ExtraArgs);
    }

    [Fact]
    public async Task Given_TheSharedBitcoind_When_UsedAsATopologyChain_Then_ItExposesItsEndpointsAndDrivesTheChain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" });
        var handle = new Cluster.Nodes.KubeNodeHandle(client, "nltg-spike-r1", "miner", NodeKind.BitcoinCore);
        var bitcoin = new FakeBitcoinCore();
        var chain = new BitcoinCoreTopologyChain(new BitcoinCoreNode(handle, new BitcoinCoreOptions { RpcUser = "u" }),
                                                 new RegtestChain(bitcoin));

        // Act
        var mined = await chain.MineAsync(3, ct);
        var height = await chain.GetBlockCountAsync(ct);
        var txId = await chain.SendToAddressAsync("bcrt1qexample", 50_000, ct);

        // Assert
        Assert.Same(handle, chain.Node);
        Assert.Equal("miner", chain.RpcHost);
        Assert.Equal(BitcoinCorePorts.Rpc, chain.RpcPort);
        Assert.Equal("u", chain.RpcUser);
        Assert.Equal("nltg", chain.RpcPassword);
        Assert.Equal(BitcoinCorePorts.ZmqRawBlock, chain.ZmqRawBlockPort);
        Assert.Equal(BitcoinCorePorts.ZmqRawTx, chain.ZmqRawTxPort);
        Assert.Equal(3, mined.Count);
        Assert.Equal(3, height);
        Assert.False(string.IsNullOrEmpty(txId));
        Assert.Contains(bitcoin.Calls, c => c.Method == "sendtoaddress" && (long)c.Args[1]! == 50_000);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => chain.MineAsync(0, ct));
    }
}