using System.Net;

namespace NLightning.Testing.Cluster.Tests.Reach;

using Cluster.Reach;

public class HostEndpointsTests
{
    [Fact]
    public void Given_NoOverride_When_ThePodFacingHostIsRead_Then_ItIsHostOrbInternal()
    {
        // Act
        var host = HostEndpoints.ForPods(_ => null);

        // Assert
        Assert.Equal(HostEndpoints.OrbStackHost, host);
        Assert.Equal(IPAddress.Loopback, HostEndpoints.BindAddressFor(host));
    }

    [Fact]
    public void Given_AnOverride_When_ThePodFacingHostIsRead_Then_ItWinsAndTheListenerBindsAllInterfaces()
    {
        // Act
        var host = HostEndpoints.ForPods(k => k == HostEndpoints.HostAddressVariable ? " 10.0.0.5 " : null);

        // Assert
        Assert.Equal("10.0.0.5", host);
        Assert.Equal(IPAddress.Any, HostEndpoints.BindAddressFor(host));
        Assert.Equal(IPAddress.Loopback, HostEndpoints.BindAddressFor(HostEndpoints.DockerHost));
    }

    [Fact]
    public void Given_HostAndNodeAddresses_When_CandidatesAreBuilt_Then_TheyAreOrderedAndDistinct()
    {
        // Act
        var candidates = HostEndpoints.Candidates(
            [IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.139.3"), IPAddress.Parse("192.168.1.10")],
            "192.168.139.2", _ => null);

        // Assert
        Assert.Equal(
            [
                HostEndpoints.OrbStackHost, HostEndpoints.DockerHost, "192.168.139.2", "192.168.1.10",
                "192.168.139.3"
            ],
            candidates);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.194.0", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    public void Given_AnAddress_When_Classified_Then_OnlyRfc1918IsPrivate(string address, bool expected) =>
        Assert.Equal(expected, HostEndpoints.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void Given_ThisMachine_When_ItsPrivateAddressesAreListed_Then_TheyAreAllPrivateIPv4()
    {
        // Act
        var addresses = HostEndpoints.LocalPrivateIPv4Addresses();

        // Assert
        Assert.All(addresses, a => Assert.True(HostEndpoints.IsPrivate(a)));
        Assert.True(addresses.Count <= 4);
    }
}