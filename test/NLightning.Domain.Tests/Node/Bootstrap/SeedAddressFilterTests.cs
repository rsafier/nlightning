using System.Net;

namespace NLightning.Domain.Tests.Node.Bootstrap;

using Domain.Node.Bootstrap;

public class SeedAddressFilterTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Given_ANonRoutableAddress_When_Checked_Then_ItIsRefusedWithAReason(string address)
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse(address), 9735, false, out var reason);

        // Assert
        Assert.False(usable);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:db9::1")]
    [InlineData("fec0::1")]
    public void Given_APublicAddress_When_Checked_Then_ItIsUsable(string address)
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse(address), 9735, false, out var reason);

        // Assert
        Assert.True(usable);
        Assert.Empty(reason);
    }

    [Fact]
    public void Given_PortZero_When_Checked_Then_ItIsRefusedEvenWithNonRoutableAllowed()
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse("1.2.3.4"), 0, true, out var reason);

        // Assert
        Assert.False(usable);
        Assert.Equal("port 0", reason);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("::1")]
    public void Given_NonRoutableAllowed_When_APrivateAddressIsChecked_Then_ItIsUsable(string address)
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse(address), 9735, true, out _);

        // Assert
        Assert.True(usable);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    public void Given_NonRoutableAllowed_When_AnUnspecifiedOrMulticastAddressIsChecked_Then_ItIsStillRefused(
        string address)
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse(address), 9735, true, out _);

        // Assert
        Assert.False(usable);
    }
}