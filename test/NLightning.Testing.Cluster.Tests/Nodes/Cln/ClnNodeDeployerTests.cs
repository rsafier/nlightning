namespace NLightning.Testing.Cluster.Tests.Nodes.Cln;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.BitcoinCore;
using Cluster.Nodes.Cln;
using Cluster.Run;
using Cluster.Topology;
using Topology;

public class ClnNodeDeployerTests
{
    [Fact]
    public void Given_AChainAndANodeSpec_When_TheOptionsAreBuilt_Then_ClnPointsAtTheChainNodeByItsAlias()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore));
        var spec = new TopologyNodeSpec("alice", NodeKind.Cln, ExtraArgs: ["--experimental-splicing"]);

        // Act
        var options = ClnNodeDeployer.BuildOptions(chain, spec);

        // Assert
        Assert.Equal("miner", options.BitcoindHost);
        Assert.Equal(18443, options.BitcoindRpcPort);
        Assert.Equal("user", options.BitcoindRpcUser);
        Assert.Equal("secret", options.BitcoindRpcPassword);
        Assert.Equal(ImageVersions.Cln, options.Image);
        Assert.Equal(["--experimental-splicing"], options.ExtraArgs);
    }

    [Fact]
    public void Given_AnImageOverride_When_TheOptionsAreBuilt_Then_ItIsUsed()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore));
        var image = new ImageRef("elementsproject/lightningd", "v26.06.7");

        // Act
        var options = ClnNodeDeployer.BuildOptions(chain, new TopologyNodeSpec("alice", NodeKind.Cln, image));

        // Assert
        Assert.Same(image, options.Image);
        Assert.Empty(options.ExtraArgs);
    }

    [Fact]
    public void Given_TheDeployer_When_AskedItsKind_Then_ItIsCln()
    {
        // Act + Assert
        Assert.Equal(NodeKind.Cln, new ClnNodeDeployer().Kind);
    }

    [Fact]
    public void Given_AChainEndpointAndEphemeralStorage_When_TheWorkloadIsBuilt_Then_ItWaitsForTheChainOnAnEmptyDir()
    {
        // Arrange
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("miner", NodeKind.BitcoinCore));
        var spec = new TopologyNodeSpec("alice", NodeKind.Cln, Storage: NodeStorage.Ephemeral);

        // Act
        var options = ClnNodeDeployer.BuildOptions(endpoint, spec);
        var pod = ClnNode.Workload("alice", options).Build(RunIdentity.Create(new TestRunOptions { RunId = "r1" }, DateTimeOffset.UnixEpoch))
                     .StatefulSet.Spec.Template.Spec;

        // Assert
        Assert.Equal(NodeStorage.Ephemeral, options.Storage);
        var wait = Assert.Single(pod.InitContainers);
        Assert.Equal(BitcoinCoreWorkload.StartupWaitContainerName, wait.Name);
        Assert.Contains(pod.Volumes, v => v.Name == DataVolume.VolumeName && v.EmptyDir is not null);
        Assert.True(new ClnNodeDeployer().DeploysWithChain);
    }

    [Fact]
    public void Given_ANodeSpecWithoutStorage_When_TheOptionsAreBuilt_Then_TheDataIsOnAPvc()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore));

        // Act
        var options = ClnNodeDeployer.BuildOptions(chain, new TopologyNodeSpec("alice", NodeKind.Cln));

        // Assert
        Assert.Equal(NodeStorage.Persistent, options.Storage);
        Assert.Null(options.StartupWait);
    }
}