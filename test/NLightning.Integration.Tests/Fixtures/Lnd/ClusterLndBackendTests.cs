namespace NLightning.Integration.Tests.Fixtures.Lnd;

using Testing.Cluster.Topology.Lnd;

public class ClusterLndBackendTests
{
    [Fact]
    public async Task Given_ABackendNotStarted_When_ItsMembersAreRead_Then_TheySayItIsNotRunningAndItDisposes()
    {
        // Arrange
        var backend = new ClusterLndBackend();

        // Act & Assert: no cluster call before StartAsync; the members fail with a message, disposal is a no-op
        Assert.Equal(TestBackendKind.Cluster, backend.Kind);
        Assert.Throws<InvalidOperationException>(() => backend.Bitcoin);
        Assert.Throws<InvalidOperationException>(() => backend.BitcoinZmqPorts);
        Assert.Throws<InvalidOperationException>(() => backend.Network);
        Assert.Throws<InvalidOperationException>(() => backend.LndNodes);
        Assert.Throws<InvalidOperationException>(() => backend.GetLndNode("alice"));
        Assert.Empty(backend.Deployer.Nodes);
        await backend.DisposeAsync();
    }

    [Fact]
    public void Given_TheDefaultNetwork_When_ComparedWithTheDockerFixture_Then_NamesAndImageMatch()
    {
        // Assert: the aliases the Docker fixture exposes (LightningRegtestNetworkFixture.LndAliases)
        Assert.Equal(LightningRegtestNetworkFixture.LndAliases, LndRegtestNetworkSpec.Default.Aliases);
        Assert.Equal(LightningRegtestNetworkFixture.ContainerNames,
                     [LndRegtestNetworkSpec.Default.ChainName, .. LndRegtestNetworkSpec.Default.Aliases]);
        Assert.Equal(LightningRegtestNetworkFixture.LndImageName, Testing.Cluster.Images.ImageVersions.Lnd.Repository);
        Assert.Equal(LightningRegtestNetworkFixture.LndImageTag, Testing.Cluster.Images.ImageVersions.Lnd.Tag);
    }
}