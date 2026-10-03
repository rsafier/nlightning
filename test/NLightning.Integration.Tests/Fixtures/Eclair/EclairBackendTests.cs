namespace NLightning.Integration.Tests.Fixtures.Eclair;

using Testing.Cluster.Images;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Eclair;
using Testing.Cluster.Topology;

public class EclairBackendTests
{
    [Fact]
    public void Given_TheFixturesEclair_When_ItsDockerConfigIsBuilt_Then_ItIsTheConfigBeforeTheBackendSplit()
    {
        // Arrange: the eclair.conf EclairFixture copied into its container before the backend split
        const string expected = """
                                eclair.chain = "regtest"
                                eclair.server.port = 9735
                                eclair.api.enabled = true
                                eclair.api.binding-ip = "0.0.0.0"
                                eclair.api.port = 8080
                                eclair.api.password = "nltg"
                                eclair.bitcoind.host = "nltg-eclair-bitcoind"
                                eclair.bitcoind.rpcport = 18443
                                eclair.bitcoind.rpcuser = "nltg"
                                eclair.bitcoind.rpcpassword = "nltg"
                                eclair.bitcoind.wallet = "eclair"
                                eclair.bitcoind.zmqblock = "tcp://nltg-eclair-bitcoind:28336"
                                eclair.bitcoind.zmqtx = "tcp://nltg-eclair-bitcoind:28335"
                                eclair.node-alias = "nltg-eclair"
                                eclair.channel.min-depth-blocks = 6
                                """;

        // Act
        var config = DockerEclairBackend.BuildConfig(EclairFixture.EclairContainerName, "eclair");

        // Assert
        Assert.Equal(expected.ReplaceLineEndings("\n"), config.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Given_TheClusterTopology_When_EclairsConfigIsBuilt_Then_OnlyTheChainsAddressDiffersFromDocker()
    {
        // Arrange: the cluster's Eclair on the topology's chain (alias miner, the harness's ZMQ ports)
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore,
                                                                                 ImageVersions.BitcoinCore31));
        var options = EclairNodeDeployer.BuildOptions(endpoint, new TopologyNodeSpec(EclairFixture.EclairContainerName,
                                                                                    NodeKind.Eclair));

        // Act
        var cluster = EclairNode.BuildConfig(EclairFixture.EclairContainerName, options).Split('\n');
        var docker = DockerEclairBackend.BuildConfig(EclairFixture.EclairContainerName, "eclair")
                                        .ReplaceLineEndings("\n").Split('\n');

        // Assert
        Assert.Equal(docker.Length, cluster.Length);
        var differing = docker.Zip(cluster).Where(p => p.First != p.Second).Select(p => p.Second).ToList();
        Assert.Equal(
        [
            "eclair.bitcoind.host = \"miner\"",
            "eclair.bitcoind.zmqblock = \"tcp://miner:28334\"",
            "eclair.bitcoind.zmqtx = \"tcp://miner:28333\""
        ], differing);
        Assert.Equal(EclairFixture.ApiPassword, options.ApiPassword);
        Assert.Equal($"{EclairFixture.EclairImage}:{EclairFixture.EclairTag}", options.Image.Reference);
        Assert.Equal(ImagePullPolicy.Never, options.Image.PullPolicy);
    }
}