namespace NLightning.Integration.Tests.Fixtures.Eclair;

using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Eclair;
using Testing.Cluster.Topology;

public class EclairBackendTests
{
    /// <summary>
    /// The <c>eclair.conf</c> the fixture's Eclair always ran with (wallet <c>eclair</c>), on the run's chain (alias
    /// <c>miner</c>, the harness's ZMQ ports: <c>hashblock</c> 28334, <c>rawtx</c> 28333).
    /// </summary>
    private static string ExpectedConfig(string alias, string wallet) =>
        $"""
         eclair.chain = "regtest"
         eclair.server.port = 9735
         eclair.api.enabled = true
         eclair.api.binding-ip = "0.0.0.0"
         eclair.api.port = 8080
         eclair.api.password = "nltg"
         eclair.bitcoind.host = "miner"
         eclair.bitcoind.rpcport = 18443
         eclair.bitcoind.rpcuser = "nltg"
         eclair.bitcoind.rpcpassword = "nltg"
         eclair.bitcoind.wallet = "{wallet}"
         eclair.bitcoind.zmqblock = "tcp://miner:28334"
         eclair.bitcoind.zmqtx = "tcp://miner:28333"
         eclair.node-alias = "{alias}"
         eclair.channel.min-depth-blocks = 6
         """.ReplaceLineEndings("\n");

    [Fact]
    public void Given_TheClusterTopology_When_EclairsConfigIsBuilt_Then_ItIsTheFixturesConfig()
    {
        // Arrange: the cluster's Eclair on the topology's chain (alias miner, the harness's ZMQ ports)
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore,
                                                                                 ImageVersions.BitcoinCore31));
        var options = EclairNodeDeployer.BuildOptions(endpoint, new TopologyNodeSpec(EclairFixture.EclairContainerName,
                                                                                    NodeKind.Eclair));

        // Act
        var config = EclairNode.BuildConfig(EclairFixture.EclairContainerName, options);

        // Assert
        Assert.Equal(ExpectedConfig("nltg-eclair", "eclair"), config.ReplaceLineEndings("\n"));
        Assert.Equal(EclairFixture.ApiPassword, options.ApiPassword);
        Assert.Equal($"{EclairFixture.EclairImage}:{EclairFixture.EclairTag}", options.Image.Reference);
        Assert.Equal(ImagePullPolicy.Never, options.Image.PullPolicy);
    }

    [Fact]
    public void Given_TheClusterTopology_When_TheSellersConfigIsBuilt_Then_ItIsTheCommonConfigPlusTheLiquidityAdsSection()
    {
        // Arrange: the seller on the cluster topology's chain (alias miner, the harness's ZMQ ports)
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore,
                                                                                 ImageVersions.BitcoinCore31));
        var options = ClusterEclairBackend.BuildSellerOptions(endpoint, EclairFixture.SellerRates);
        const string section = """
                               eclair.liquidity-ads {
                                 funding-rates = [
                                   {
                                     min-funding-amount-satoshis = 10000
                                     max-funding-amount-satoshis = 5000000
                                     funding-weight = 400
                                     fee-base-satoshis = 500
                                     fee-basis-points = 100
                                     channel-creation-fee-satoshis = 1000
                                   }
                                 ]
                                 payment-types = ["from_channel_balance"]
                                 lock-utxos-during-funding = true
                               }
                               """;

        // Act
        var config = EclairNode.BuildConfig(EclairFixture.SellerContainerName, options).ReplaceLineEndings("\n");

        // Assert: the seller's own wallet and alias, its section last; the seller is never restarted
        Assert.Equal(ExpectedConfig("nltg-eclair-seller", "eclair-seller") + "\n" + section.ReplaceLineEndings("\n"),
                     config);
        Assert.Equal("eclair-seller", options.Wallet);
        Assert.Equal(NodeStorage.Ephemeral, options.Storage);
        Assert.Equal($"{EclairFixture.EclairImage}:{EclairFixture.EclairTag}", options.Image.Reference);
    }
}