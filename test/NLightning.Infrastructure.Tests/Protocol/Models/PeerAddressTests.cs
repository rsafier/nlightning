using System.Net;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Gossip.Addresses;

namespace NLightning.Infrastructure.Tests.Protocol.Models;

using Infrastructure.Protocol.Models;

public class PeerAddressTests
{
    [Fact]
    public void Given_SingleStringAddress_When_ConstructingPeerAddress_Then_PropertiesAreCorrectlyInitialized()
    {
        // Arrange
        const string address = "028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@127.0.0.1:8080";

        // Act
        var peerAddress = new PeerAddress(address);

        // Assert
        Assert.Equal("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7",
                     peerAddress.PubKey.ToString());
        Assert.Equal(IPAddress.Parse("127.0.0.1"), peerAddress.IpAddress);
        Assert.Equal(8080, peerAddress.Port);
    }

    [Fact]
    public void Given_HttpAddress_When_ConstructingPeerAddress_Then_HostAndPortAreCorrectlyResolved()
    {
        // Arrange
        // A host name is kept as written (resolved when a direct dial connects), so this stays hermetic
        CompactPubKey pubKey =
            Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");
        const string address = "http://localhost:8080/";

        // Act
        var peerAddress = new PeerAddress(pubKey, address);

        // Assert
        Assert.Equal(pubKey, peerAddress.PubKey);
        Assert.Equal("localhost", peerAddress.Host);
        Assert.Equal(AddressDescriptorType.Dns, peerAddress.Type);
        Assert.Equal(8080, peerAddress.Port);
    }

    [Fact]
    public void Given_SingleStringHttpAddress_When_ConstructingPeerAddress_Then_HostAndPortAreCorrectlyResolved()
    {
        // Arrange
        const string address = "028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@http://localhost:9735/";

        // Act
        var peerAddress = new PeerAddress(address);

        // Assert
        Assert.Equal("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7",
                     peerAddress.PubKey.ToString());
        Assert.Equal("localhost", peerAddress.Host);
        Assert.Null(peerAddress.IpAddress);
        Assert.Equal(9735, peerAddress.Port);
    }

    [Fact]
    public void Given_PubKeyHostAndPort_When_ConstructingPeerAddress_Then_PropertiesAreCorrectlyInitialized()
    {
        // Arrange
        CompactPubKey pubKey =
            Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");
        const string host = "127.0.0.1";
        const int port = 8080;

        // Act
        var peerAddress = new PeerAddress(pubKey, host, port);

        // Assert
        Assert.Equal(pubKey, peerAddress.PubKey);
        Assert.Equal(IPAddress.Parse(host), peerAddress.IpAddress);
        Assert.Equal(port, peerAddress.Port);
    }

    [Fact]
    public void Given_PeerAddressInstance_When_CallingToString_Then_ReturnsExpectedFormat()
    {
        // Arrange
        CompactPubKey pubKey =
            Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");
        const string host = "127.0.0.1";
        const int port = 8080;
        var peerAddress = new PeerAddress(pubKey, host, port);

        // Act
        var result = peerAddress.ToString();

        // Assert
        Assert.Equal("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@127.0.0.1:8080", result);
    }

    [Fact]
    public void Given_ABracketedIPv6Address_When_ConstructingPeerAddress_Then_HostAndPortAreRead()
    {
        // Arrange
        const string address =
            "028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@[2001:db8::1]:9735";

        // Act
        var peerAddress = new PeerAddress(address);

        // Assert
        Assert.Equal(IPAddress.Parse("2001:db8::1"), peerAddress.IpAddress);
        Assert.Equal(9735, peerAddress.Port);
        Assert.Equal(address, peerAddress.ToString());
    }

    [Fact]
    public void Given_ABareIPv4Address_When_ConstructingPeerAddress_Then_ItIsReadAsBefore()
    {
        // Arrange
        const string address = "028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@1.2.3.4:9766";

        // Act
        var peerAddress = new PeerAddress(address);

        // Assert
        Assert.Equal(IPAddress.Parse("1.2.3.4"), peerAddress.IpAddress);
        Assert.Equal(9766, peerAddress.Port);
        Assert.Equal(address, peerAddress.ToString());
    }

    [Theory]
    [InlineData("[2001:db8::1:9735")]
    [InlineData("[2001:db8::1]9735")]
    [InlineData("2001:db8::1:9735")]
    [InlineData("[1.2.3.4]:9735")]
    [InlineData("[2001:db8::1]:0")]
    [InlineData("1.2.3.4:70000")]
    public void Given_AMalformedHost_When_ConstructingPeerAddress_Then_ItThrowsFormatException(string hostPort)
    {
        // Arrange
        var address = $"028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@{hostPort}";

        // Act & Assert
        Assert.Throws<FormatException>(() => new PeerAddress(address));
    }

    private const string OnionHost = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    [Fact]
    public void Given_AnOnionAddress_When_ConstructingPeerAddress_Then_ItIsATorV3HostNeverResolved()
    {
        // Arrange
        var address = $"028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@{OnionHost.ToUpperInvariant()}:9735";

        // Act
        var peerAddress = new PeerAddress(address);

        // Assert
        Assert.Equal(OnionHost, peerAddress.Host);
        Assert.Equal(AddressDescriptorType.TorV3, peerAddress.Type);
        Assert.True(peerAddress.IsOnion);
        Assert.Null(peerAddress.IpAddress);
        Assert.Equal(9735, peerAddress.Port);
        Assert.Equal($"028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@{OnionHost}:9735",
                     peerAddress.ToString());
    }

    [Fact]
    public void Given_ADnsHostName_When_ConstructingPeerAddress_Then_ItIsKeptUnresolved()
    {
        // Arrange
        const string address = "028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@node.example.com:9735";

        // Act
        var peerAddress = new PeerAddress(address);

        // Assert
        Assert.Equal("node.example.com", peerAddress.Host);
        Assert.Equal(AddressDescriptorType.Dns, peerAddress.Type);
        Assert.Equal(address, peerAddress.ToString());
    }

    [Theory]
    [InlineData("duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczae.onion:9735")]
    [InlineData("euckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion:9735")]
    [InlineData("expyuzz4wqqyqhjn.onion:9735")]
    [InlineData("bad_host!:9735")]
    [InlineData("h\u00f6st.example:9735")]
    public void Given_ABadOnionOrHostName_When_ConstructingPeerAddress_Then_ItThrowsFormatException(string hostPort)
    {
        // Arrange
        var address = $"028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@{hostPort}";

        // Act & Assert
        Assert.Throws<FormatException>(() => new PeerAddress(address));
    }

    [Fact]
    public void Given_TheSameOnionInAnotherCase_When_Compared_Then_TheAddressesAreEqual()
    {
        // Arrange
        CompactPubKey pubKey =
            Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");

        // Act
        var lower = new PeerAddress(pubKey, OnionHost, 9735);
        var upper = new PeerAddress(pubKey, OnionHost.ToUpperInvariant(), 9735);

        // Assert
        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }
}