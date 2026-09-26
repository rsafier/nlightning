namespace NLightning.Domain.Tests.Node.Options;

using Domain.Gossip.Addresses;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

public class GossipOptionsTests
{
    [Fact]
    public void Given_Defaults_When_Read_Then_NoAddressIsAnnouncedAndOwnGossipFlushesEveryMinute()
    {
        // Arrange
        var options = new GossipOptions();

        // Act / Assert (plan D11: the listen addresses are never announced on their own)
        Assert.Empty(options.AnnounceAddresses);
        Assert.Empty(options.GetAnnounceAddressDescriptors());
        Assert.Empty(options.GetValidationErrors());
        Assert.Equal(TimeSpan.FromSeconds(60), options.OwnGossipFlushInterval);
        Assert.Equal(TimeSpan.FromDays(13), options.NodeAnnouncementRefreshInterval);
    }

    [Fact]
    public void Given_MixedAddresses_When_Parsed_Then_TheyAreInAscendingTypeOrder()
    {
        // Arrange (BOLT 7: MUST place address descriptors in ascending order)
        var options = new GossipOptions
        {
            AnnounceAddresses =
            [
                "node.example.com:9735",
                "[2001:db8::1]:9736",
                "vww6ybal4bd7szmgncyruucpgfkqahzddi37ktceo3ah7ngmcopnpyyd.onion:9737",
                "203.0.113.5:9735"
            ]
        };

        // Act
        var descriptors = options.GetAnnounceAddressDescriptors();

        // Assert
        Assert.Equal([AddressDescriptorType.IPv4, AddressDescriptorType.IPv6, AddressDescriptorType.TorV3,
                         AddressDescriptorType.Dns], descriptors.Select(d => d.Type));
        Assert.Equal("203.0.113.5", descriptors[0].Host);
        Assert.Equal((ushort)9736, descriptors[1].Port);
        Assert.Equal("node.example.com", descriptors[3].Host);
    }

    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("203.0.113.5:0")]
    [InlineData("203.0.113.5:70000")]
    [InlineData("2001:db8::1:9735")]
    [InlineData("expyuzz4wqqyqhjn.onion:9735")]
    [InlineData(" ")]
    public void Given_ABadAddress_When_Validated_Then_ItIsAnError(string address)
    {
        // Arrange
        var options = new GossipOptions { AnnounceAddresses = [address] };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        var error = Assert.Single(errors);
        Assert.StartsWith("Gossip:AnnounceAddresses", error);
        Assert.Throws<ArgumentException>(() => options.GetAnnounceAddressDescriptors());
    }

    [Fact]
    public void Given_TwoDnsNames_When_Validated_Then_ItIsAnError()
    {
        // Arrange (BOLT 7: MUST NOT announce more than one type 5 DNS hostname)
        var options = new GossipOptions { AnnounceAddresses = ["a.example.com:9735", "b.example.com:9735"] };

        // Act / Assert
        Assert.Single(options.GetValidationErrors());
    }

    [Theory]
    [InlineData("mainnet", false, false)]
    [InlineData("mainnet", true, true)]
    [InlineData("signet", false, true)]
    [InlineData("regtest", false, true)]
    public void Given_ANetwork_When_Checked_Then_PublicChannelsFollowTheMainnetGate(string network, bool allow,
                                                                                    bool expected)
    {
        // Arrange (plan D12)
        var options = new GossipOptions { AllowPublicChannelsOnMainnet = allow };

        // Act / Assert
        Assert.Equal(expected, options.ArePublicChannelsAllowed(new BitcoinNetwork(network)));
    }
}