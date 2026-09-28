using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Protocol.Dns;

using Domain.Node.Options;
using Infrastructure.Protocol.Dns;

public class DnsClientRecordLookupTests
{
    [Fact]
    public void Given_Options_When_TheLookupIsConstructed_Then_NoResolverIsCreated()
    {
        // Arrange
        var nodeOptions = new NodeOptions { Bootstrap = { NameServers = ["1.1.1.1"] } };

        // Act
        var lookup = new DnsClientRecordLookup(Options.Create(nodeOptions),
                                               NullLogger<DnsClientRecordLookup>.Instance);

        // Assert: the LookupClient (system resolver discovery, sockets) is built on the first query only
        Assert.False(lookup.IsClientCreated);
    }

    [Fact]
    public void Given_TheDefaultTransport_When_BuildingTheClientOptions_Then_TcpOnlyWithoutCacheAndA4096Buffer()
    {
        // Arrange
        var options = new BootstrapOptions
        {
            NameServers = ["1.1.1.1", "8.8.8.8:5353", "[2606:4700:4700::1111]:53"],
            QueryTimeout = TimeSpan.FromSeconds(2)
        };

        // Act
        var clientOptions = DnsClientRecordLookup.BuildOptions(options);

        // Assert
        Assert.True(clientOptions.UseTcpOnly);
        Assert.True(clientOptions.UseTcpFallback);
        Assert.Equal(4096, clientOptions.ExtendedDnsBufferSize);
        Assert.False(clientOptions.UseCache);
        Assert.False(clientOptions.ThrowDnsErrors);
        Assert.True(clientOptions.ContinueOnDnsError);
        Assert.Equal(1, clientOptions.Retries);
        Assert.Equal(TimeSpan.FromSeconds(2), clientOptions.Timeout);
        Assert.Equal([("1.1.1.1", 53), ("8.8.8.8", 5353), ("2606:4700:4700::1111", 53)],
                     clientOptions.NameServers.Select(n => (n.Address, n.Port)).ToArray());
    }

    [Fact]
    public void Given_UdpWithTcpFallback_When_BuildingTheClientOptions_Then_UdpIsAllowedWithTheTcpRetry()
    {
        // Arrange
        var options = new BootstrapOptions
        {
            NameServers = ["1.1.1.1"],
            Transport = DnsSeedTransport.UdpWithTcpFallback
        };

        // Act
        var clientOptions = DnsClientRecordLookup.BuildOptions(options);

        // Assert
        Assert.False(clientOptions.UseTcpOnly);
        Assert.True(clientOptions.UseTcpFallback);
        Assert.Equal(4096, clientOptions.ExtendedDnsBufferSize);
    }
}