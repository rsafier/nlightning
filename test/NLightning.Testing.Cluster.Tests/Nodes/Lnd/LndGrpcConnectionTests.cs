namespace NLightning.Testing.Cluster.Tests.Nodes.Lnd;

using Cluster.Nodes.Lnd;

public class LndGrpcConnectionTests
{
    [Theory]
    [InlineData("192.168.194.26", 10009, "https://192.168.194.26:10009/")]
    [InlineData("fd07:b51a:cc66::1a", 10009, "https://[fd07:b51a:cc66::1a]:10009/")]
    [InlineData("[fd07::1]", 10009, "https://[fd07::1]:10009/")]
    [InlineData("alice-0.alice.nltg-spike-r1.svc.cluster.local", 10009,
                "https://alice-0.alice.nltg-spike-r1.svc.cluster.local:10009/")]
    public void Given_AHost_When_TheEndpointIsBuilt_Then_ItIsAnHttpsUri(string host, int port, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, LndGrpcConnection.BuildEndpoint(host, port).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Given_ABadPort_When_TheEndpointIsBuilt_Then_ItThrows(int port)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => LndGrpcConnection.BuildEndpoint("alice", port));
    }
}