namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Nodes;
using Cluster.Run;
using Cluster.Topology;

public class StableNodeAddressTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "cln" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_ANode_When_ItsStableServiceIsBuilt_Then_ItIsAClusterIpServiceSelectingTheNodesPod()
    {
        // Act
        var service = StableNodeAddress.Build(s_run, "bob", NodeKind.Cln, 9735);

        // Assert
        Assert.Equal("bob-p2p", service.Metadata.Name);
        Assert.Equal("nltg-spike-r1", service.Metadata.NamespaceProperty);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.Null(service.Spec.ClusterIP);
        Assert.Equal(RunLabels.NodeSelector("r1", "bob"), service.Spec.Selector);
        Assert.Equal("cln", service.Metadata.Labels[RunLabels.Kind]);
        Assert.Equal("r1", service.Metadata.Labels[RunLabels.Run]);
        var port = Assert.Single(service.Spec.Ports);
        Assert.Equal(9735, port.Port);
        Assert.Equal("9735", port.TargetPort.Value);
        Assert.NotEqual(true, service.Spec.PublishNotReadyAddresses);
    }

    [Fact]
    public void Given_ANode_When_ItsStableNameIsRead_Then_ItIsTheServicesDnsName()
    {
        // Act + Assert
        Assert.Equal("bob-p2p.nltg-spike-r1.svc.cluster.local", StableNodeAddress.DnsName(s_run, "bob"));
    }

    [Fact]
    public void Given_AnInvalidNodeName_When_TheServiceNameIsBuilt_Then_ItIsRefused()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => StableNodeAddress.ServiceName("Bob"));
        Assert.Throws<ArgumentException>(() => StableNodeAddress.ServiceName(new string('a', 53)));
    }
}