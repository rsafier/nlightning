using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Nodes.Eclair;

using Cluster.Diagnostics;
using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.BitcoinCore;
using Cluster.Nodes.Eclair;
using Cluster.Run;
using Cluster.Topology;
using Topology;

public class EclairNodeTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "eclair" }, DateTimeOffset.UnixEpoch);

    private static EclairNodeOptions Options() =>
        new() { BitcoindHost = "miner", BitcoindRpcUser = "nltg", BitcoindRpcPassword = "pw" };

    [Fact]
    public void Given_AnEclairNode_When_ItsWorkloadIsBuilt_Then_ItRunsTheLocalImageWithItsDataOnAPvc()
    {
        // Act
        var workload = EclairNode.Workload("carol", Options());

        // Assert
        Assert.Equal(NodeKind.Eclair, workload.Kind);
        Assert.Equal(ImageVersions.Eclair, workload.Image);
        Assert.Equal(ImagePullPolicy.Never, workload.Image.PullPolicy);
        Assert.Equal(new DataVolume("/data", "1Gi"), workload.Data);
        Assert.Equal([new WorkloadPort("p2p", 9735), new WorkloadPort("api", 8080)], workload.Ports);
        Assert.Equal("-Xmx512m -Declair.printToConsole=true", workload.Env["JAVA_OPTS"]);
        Assert.Equal("nltg", workload.Env[EclairNode.ApiPasswordVariable]);
        Assert.Equal(EclairNode.StartCommand, workload.Command);
        Assert.Contains("exec eclair-node/bin/eclair-node.sh -Declair.datadir=/data", workload.Command![2],
                        StringComparison.Ordinal);
    }

    [Fact]
    public void Given_EclairOptions_When_TheConfigIsBuilt_Then_ItMatchesTheDockerFixtureOnTheClusterChain()
    {
        // Act
        var config = EclairNode.BuildConfig("carol", Options() with { ExtraConfig = ["eclair.features.foo = 1"] });

        // Assert
        Assert.Equal(
            """
            eclair.chain = "regtest"
            eclair.server.port = 9735
            eclair.api.enabled = true
            eclair.api.binding-ip = "0.0.0.0"
            eclair.api.port = 8080
            eclair.api.password = "nltg"
            eclair.bitcoind.host = "miner"
            eclair.bitcoind.rpcport = 18443
            eclair.bitcoind.rpcuser = "nltg"
            eclair.bitcoind.rpcpassword = "pw"
            eclair.bitcoind.wallet = "eclair"
            eclair.bitcoind.zmqblock = "tcp://miner:28334"
            eclair.bitcoind.zmqtx = "tcp://miner:28333"
            eclair.node-alias = "carol"
            eclair.channel.min-depth-blocks = 6
            eclair.features.foo = 1
            """.ReplaceLineEndings("\n"), config);
    }

    [Fact]
    public void Given_AWorkload_When_Built_Then_TheWalletInitRunsAfterTheChainWaitInEclairsImage()
    {
        // Arrange
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore,
                                                                                 ImageVersions.BitcoinCore31));
        var options = EclairNodeDeployer.BuildOptions(endpoint, new TopologyNodeSpec("carol", NodeKind.Eclair));

        // Act
        var pod = EclairNode.Workload("carol", options).Build(s_run).StatefulSet.Spec.Template.Spec;

        // Assert
        Assert.Equal([BitcoinCoreWorkload.StartupWaitContainerName, EclairNode.WalletInitContainerName],
                     pod.InitContainers.Select(c => c.Name));
        var wallet = pod.InitContainers[1];
        Assert.Equal(ImageVersions.Eclair.Reference, wallet.Image);
        Assert.Equal("Never", wallet.ImagePullPolicy);
        Assert.Equal("eclair", wallet.Env.Single(e => e.Name == "NLTG_WALLET").Value);
        Assert.Equal("miner", wallet.Env.Single(e => e.Name == "NLTG_RPC_HOST").Value);
        Assert.Contains("createwallet", wallet.Command[2], StringComparison.Ordinal);
        Assert.Contains("initialblockdownload == false", wallet.Command[2], StringComparison.Ordinal);
        Assert.NotNull(wallet.Resources.Requests);
    }

    [Fact]
    public void Given_AnEclairPod_When_Described_Then_NeitherTheRpcNorTheApiPasswordAppears()
    {
        // Arrange: a pod of the workload, as a failure dump describes it
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore,
                                                                                 ImageVersions.BitcoinCore31));
        var options = EclairNodeDeployer.BuildOptions(endpoint, new TopologyNodeSpec("carol", NodeKind.Eclair)) with
        {
            BitcoindRpcPassword = "rpc-hunter2",
            ApiPassword = "api-hunter3"
        };
        var template = EclairNode.Workload("carol", options).Build(s_run).StatefulSet.Spec.Template;
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "carol-0", NamespaceProperty = s_run.Namespace },
            Spec = template.Spec
        };

        // Act
        var text = ResourceDescriber.DescribePod(pod);

        // Assert: the wallet init's variables are there, the passwords are not
        Assert.Contains("NLTG_RPC_USER=", text, StringComparison.Ordinal);
        Assert.Contains("NLTG_RPC_PASSWORD=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rpc-hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("api-hunter3", text, StringComparison.Ordinal);
        Assert.Contains("$NLTG_RPC_USER:$NLTG_RPC_PASSWORD", EclairNode.WalletInitScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_AWorkload_When_Built_Then_ItDrainsBeforeStoppingAndIsReadyOnlyWhenTheApiAnswers()
    {
        // Act
        var container = EclairNode.Workload("carol", Options()).Build(s_run).StatefulSet.Spec.Template.Spec
                                  .Containers[0];

        // Assert
        Assert.Equal(EclairNode.StopCommand, container.Lifecycle.PreStop.Exec.Command);
        Assert.Contains($"touch {EclairNode.DrainFile}", EclairNode.StopCommand[2], StringComparison.Ordinal);
        var probe = container.ReadinessProbe.Exec.Command[2];
        Assert.Contains($"[ ! -f {EclairNode.DrainFile} ]", probe, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:8080/getinfo", probe, StringComparison.Ordinal);
        // The password comes from the environment, never from the probe's command line
        Assert.Contains("-u \":$NLTG_API_PASSWORD\"", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_AChainWithTheHashBlockFeed_When_TheOptionsAreBuilt_Then_EclairFollowsItAndItsStorage()
    {
        // Arrange
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore));
        var spec = new TopologyNodeSpec("carol", NodeKind.Eclair, ExtraArgs: ["eclair.x = 1"],
                                        Storage: NodeStorage.Ephemeral);

        // Act
        var options = EclairNodeDeployer.BuildOptions(endpoint, spec);

        // Assert
        Assert.Equal("miner", options.BitcoindHost);
        Assert.Equal(BitcoinCorePorts.ZmqHashBlock, options.ZmqHashBlockPort);
        Assert.Equal(BitcoinCorePorts.ZmqRawTx, options.ZmqRawTxPort);
        Assert.Equal(["eclair.x = 1"], options.ExtraConfig);
        Assert.Equal(NodeStorage.Ephemeral, options.Storage);
        Assert.NotNull(options.StartupWait);
        Assert.True(new EclairNodeDeployer().DeploysWithChain);
        Assert.Equal(NodeKind.Eclair, new EclairNodeDeployer().Kind);
    }

    [Fact]
    public void Given_AChainWithoutTheHashBlockFeed_When_TheOptionsAreBuilt_Then_ItIsRefused()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore));

        // Act + Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => EclairNodeDeployer.BuildOptions(chain, new TopologyNodeSpec("carol", NodeKind.Eclair)));
        Assert.Contains("hashblock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_ATopologyWithEclair_When_Built_Then_ItHasADeployer()
    {
        // Act
        var spec = new TopologyBuilder().AddBitcoinCore("miner", ImageVersions.BitcoinCore31).AddEclair("carol")
                                        .Build();

        // Assert
        Assert.Equal(NodeKind.Eclair, Assert.Single(spec.LightningNodes).Kind);
    }
}