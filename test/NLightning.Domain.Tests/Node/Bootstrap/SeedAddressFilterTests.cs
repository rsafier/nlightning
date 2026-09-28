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
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.254")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("::a00:1")]
    [InlineData("::7f00:1")]
    [InlineData("::8.8.8.8")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::c0a8:101")]
    [InlineData("64:ff9b::c000:201")]
    [InlineData("64:ff9b:1::808:808")]
    [InlineData("2002:a00:1::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2002:c612:1::1")]
    [InlineData("100::1")]
    [InlineData("fec0::1")]
    [InlineData("feff::1")]
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
    [InlineData("192.0.1.1")]
    [InlineData("192.0.3.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("198.51.101.1")]
    [InlineData("203.0.114.1")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    [InlineData("100:0:0:1::1")]
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
    [InlineData("192.0.2.1")]
    [InlineData("64:ff9b::a00:1")]
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
    [InlineData("64:ff9b::e000:1")]
    [InlineData("2002:0:1::1")]
    public void Given_NonRoutableAllowed_When_AnUnspecifiedOrMulticastAddressIsChecked_Then_ItIsStillRefused(
        string address)
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse(address), 9735, true, out _);

        // Assert
        Assert.False(usable);
    }

    [Theory]
    [InlineData("64:ff9b::a00:1", "NAT64 IPv6 (64:ff9b::/96) embedding private IPv4 (10.0.0.0/8)")]
    [InlineData("2002:7f00:1::", "6to4 IPv6 (2002::/16) embedding loopback IPv4 (127.0.0.0/8)")]
    [InlineData("::a00:1", "IPv4-compatible IPv6 (::/96)")]
    public void Given_AnIPv6AddressEmbeddingIPv4_When_Checked_Then_TheReasonNamesBoth(string address,
        string expected)
    {
        // Act
        var usable = SeedAddressFilter.IsUsable(IPAddress.Parse(address), 9735, false, out var reason);

        // Assert
        Assert.False(usable);
        Assert.Equal(expected, reason);
    }
}