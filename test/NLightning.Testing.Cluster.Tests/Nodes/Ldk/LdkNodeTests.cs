namespace NLightning.Testing.Cluster.Tests.Nodes.Ldk;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.Ldk;
using Cluster.Run;
using Cluster.Topology;

public class LdkNodeTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "ldk" }, DateTimeOffset.UnixEpoch);

    private static LdkNodeOptions Options() =>
        new() { BitcoindHost = "miner", BitcoindRpcUser = "nltg", BitcoindRpcPassword = "pw" };

    [Fact]
    public void Given_AnLdkNode_When_ItsWorkloadIsBuilt_Then_ItRunsTheLocalImageWithItsDataOnAPvc()
    {
        // Act
        var workload = LdkNode.Workload("ldk", Options());

        // Assert
        Assert.Equal(NodeKind.Ldk, workload.Kind);
        Assert.Equal(ImageVersions.Ldk, workload.Image);
        Assert.Equal("Never", workload.Image.PullPolicyValue);
        Assert.Equal("nltg-ldk-server:dc02b76c", workload.Image.Reference);
        Assert.Equal(new DataVolume("/data", "1Gi"), workload.Data);
        Assert.Equal([new WorkloadPort("p2p", 9735), new WorkloadPort("grpc", 3536)], workload.Ports);
        Assert.Equal(WorkloadResources.Default, workload.Resources);
        Assert.Empty(workload.InitContainers);
    }

    [Fact]
    public void Given_AnLdkWorkload_When_Built_Then_ItWritesItsConfigFromTheEnvironmentAndRunsLdkServerAsPid1()
    {
        // Act
        var workload = LdkNode.Workload("ldk", Options());

        // Assert
        Assert.Equal(LdkNode.BuildConfig("ldk", Options()), workload.Env[LdkNode.ConfigVariable]);
        Assert.NotNull(workload.Command);
        Assert.Equal(["sh", "-c"], workload.Command.Take(2));
        Assert.Equal("mkdir -p /data && printf '%s' \"$NLTG_LDK_CONFIG\" > /data/config.toml "
                   + "&& exec ldk-server /data/config.toml", workload.Command[2]);
        Assert.Empty(workload.Args);
    }

    [Fact]
    public void Given_AnLdkWorkload_When_Built_Then_ItDrainsBeforeItStopsAndItsProbeHonorsTheDrain()
    {
        // Act
        var pod = LdkNode.Workload("ldk", Options()).Build(s_run).StatefulSet.Spec.Template.Spec;

        // Assert
        var container = Assert.Single(pod.Containers);
        Assert.Equal(15, pod.TerminationGracePeriodSeconds);
        var preStop = string.Join(' ', container.Lifecycle.PreStop.Exec.Command);
        Assert.Contains($"touch {LdkNode.DrainFile}", preStop, StringComparison.Ordinal);
        Assert.Contains($"sleep {LdkNode.DrainSeconds}", preStop, StringComparison.Ordinal);
        var probe = string.Join(' ', container.ReadinessProbe.Exec.Command);
        Assert.Contains($"[ ! -f {LdkNode.DrainFile} ]", probe, StringComparison.Ordinal);
        Assert.Contains("ldk-server-cli -c /data/config.toml get-node-info", probe, StringComparison.Ordinal);
        Assert.Equal(1, container.ReadinessProbe.PeriodSeconds);
    }

    [Fact]
    public void Given_LdkOptions_When_TheConfigIsBuilt_Then_ItIsTheDockerFixturesLayoutOnTheChainsAlias()
    {
        // Arrange
        var options = Options() with { Alias = "nltg-ldk", AnnouncementAddresses = ["10.43.0.7:9735"] };

        // Act
        var config = LdkNode.BuildConfig("ldk", options);

        // Assert
        Assert.Equal("""
                     [node]
                     network = "regtest"
                     listening_addresses = ["0.0.0.0:9735"]
                     announcement_addresses = ["10.43.0.7:9735"]
                     alias = "nltg-ldk"

                     [storage.disk]
                     dir_path = "/data/ldk"

                     [log]
                     level = "Info"
                     log_to_file = false

                     [bitcoind]
                     rpc_address = "miner:18443"
                     rpc_user = "nltg"
                     rpc_password = "pw"
                     """, config);
    }

    [Fact]
    public void Given_NoAnnouncedAddressAndNoAlias_When_TheConfigIsBuilt_Then_TheLineIsLeftOutAndTheNameIsTheAlias()
    {
        // Act
        var config = LdkNode.BuildConfig("ldk", Options());

        // Assert
        Assert.DoesNotContain("announcement_addresses", config, StringComparison.Ordinal);
        Assert.Contains("alias = \"ldk\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_ASubcommand_When_TheCliCommandIsBuilt_Then_ItReadsTheNodesConfig()
    {
        // Act
        var command = LdkNode.CliCommand("open-channel", ["02ab", "10.0.0.1:9735", "1000sat"]);

        // Assert
        Assert.Equal(["ldk-server-cli", "-c", "/data/config.toml", "open-channel", "02ab", "10.0.0.1:9735", "1000sat"],
                     command);
    }

    [Fact]
    public void Given_ATopologyNode_When_TheDeployerBuildsItsOptions_Then_ItUsesTheChainAndAnnouncesTheStableAddress()
    {
        // Arrange
        var chain = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore));
        var node = new TopologyNodeSpec("ldk", NodeKind.Ldk, ExtraArgs: ["alias=nltg-ldk", "log-level=Debug"]);

        // Act
        var options = LdkNodeDeployer.BuildOptions(chain, node, "10.43.0.7");

        // Assert
        Assert.Equal("miner", options.BitcoindHost);
        Assert.Equal(18443, options.BitcoindRpcPort);
        Assert.Equal("nltg", options.BitcoindRpcUser);
        Assert.Equal(["10.43.0.7:9735"], options.AnnouncementAddresses);
        Assert.Equal("nltg-ldk", options.Alias);
        Assert.Equal("Debug", options.LogLevel);
        Assert.Equal(NodeStorage.Persistent, options.Storage);
        Assert.NotNull(options.StartupWait);
        Assert.Equal(ImageVersions.Ldk, options.Image);
    }

    [Fact]
    public void Given_AnUnknownLdkOption_When_TheDeployerBuildsItsOptions_Then_ItIsRefused()
    {
        // Arrange
        var chain = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore));
        var node = new TopologyNodeSpec("ldk", NodeKind.Ldk, ExtraArgs: ["--announce"]);

        // Act
        var exception = Assert.Throws<ArgumentException>(() => LdkNodeDeployer.BuildOptions(chain, node, null));

        // Assert
        Assert.Contains("unknown LDK option '--announce'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_ATopologyWithAnLdkNode_When_Built_Then_TheDefaultDeployerTakesIt()
    {
        // Act
        var spec = new TopologyBuilder().AddBitcoinCore("miner").AddLdk("ldk").Build();

        // Assert
        Assert.Equal(NodeKind.Ldk, Assert.Single(spec.LightningNodes).Kind);
    }

    [Fact]
    public void Given_ANode_When_ItsStableAddressIsBuilt_Then_ItIsAClusterIpServiceOfItsP2PPort()
    {
        // Act
        var service = StableNodeAddress.Build(s_run, "ldk", NodeKind.Ldk, LdkNode.P2PPort);

        // Assert
        Assert.Equal("ldk-p2p", service.Metadata.Name);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.Equal(9735, Assert.Single(service.Spec.Ports).Port);
    }
}