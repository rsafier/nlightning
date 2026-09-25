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
}