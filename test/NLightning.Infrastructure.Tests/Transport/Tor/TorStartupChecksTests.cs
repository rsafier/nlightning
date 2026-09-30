namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Domain.Gossip.Addresses;
using Domain.Node.Options;
using Infrastructure.Transport.Tor;

public class TorStartupChecksTests
{
    [Fact]
    public void Given_ATorOnlyNodeAllowedToListenOffLoopback_When_Checked_Then_ItIsReminded()
    {
        // Arrange - NL-577: refused at validation unless allowed; once allowed, the start still says so
        var options = new NodeOptions
        {
            ListenAddresses = ["0.0.0.0:9735"],
            Tor = new TorOptions { Mode = TorMode.TorOnly, AllowClearnetListen = true }
        };

        // Act
        var warnings = TorStartupChecks.GetWarnings(options, []);

        // Assert
        Assert.Contains(warnings, w => w.Contains("0.0.0.0:9735", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_AHybridNode_When_Checked_Then_NothingIsSaid()
    {
        // Arrange
        var options = new NodeOptions
        {
            ListenAddresses = ["0.0.0.0:9735"],
            Tor = new TorOptions { Mode = TorMode.Hybrid }
        };

        // Act & Assert
        Assert.Empty(TorStartupChecks.GetWarnings(options, [AddressDescriptor.FromHost(AddressDescriptorType.IPv4,
                                                                "203.0.113.7", 9735)]));
    }
}