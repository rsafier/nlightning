using System.Net;
using NLightning.Domain.Crypto.ValueObjects;

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
        Assert.Equal(IPAddress.Parse("127.0.0.1"), peerAddress.Host);
        Assert.Equal(8080, peerAddress.Port);
    }

    [Fact]
    public void Given_HttpAddress_When_ConstructingPeerAddress_Then_HostAndPortAreCorrectlyResolved()
    {
        // Arrange
        // "localhost" resolves from the hosts file, so this stays hermetic (no live DNS).
        CompactPubKey pubKey =
            Convert.FromHexString("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7");
        const string address = "http://localhost:8080/";

        // Act
        var peerAddress = new PeerAddress(pubKey, address);

        // Assert
        Assert.Equal(pubKey, peerAddress.PubKey);
        Assert.True(IPAddress.IsLoopback(peerAddress.Host));
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
        Assert.True(IPAddress.IsLoopback(peerAddress.Host));
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
        Assert.Equal(IPAddress.Parse(host), peerAddress.Host);
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
        Assert.Equal(IPAddress.Parse("2001:db8::1"), peerAddress.Host);
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
        Assert.Equal(IPAddress.Parse("1.2.3.4"), peerAddress.Host);
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
}