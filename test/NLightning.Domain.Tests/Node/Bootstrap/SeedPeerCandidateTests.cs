using System.Net;

namespace NLightning.Domain.Tests.Node.Bootstrap;

using Domain.Crypto.ValueObjects;
using Domain.Node.Bootstrap;

public class SeedPeerCandidateTests
{
    private const string NodeIdHex = "0245869b288cf93a6a7402ba730dcb891073996f5140587a55c4e2250e7ee8d181";

    [Fact]
    public void Given_AnIPv4Candidate_When_Formatted_Then_ItIsPubkeyAtHostPort()
    {
        // Arrange
        var candidate = new SeedPeerCandidate(new CompactPubKey(Convert.FromHexString(NodeIdHex)),
                                              IPAddress.Parse("1.2.3.4"), 9766, "nodes.lightning.directory");

        // Act
        var info = candidate.ToPeerAddressInfo();

        // Assert
        Assert.Equal($"{NodeIdHex}@1.2.3.4:9766", info.Address);
    }

    [Fact]
    public void Given_AnIPv6Candidate_When_Formatted_Then_TheHostIsInBrackets()
    {
        // Arrange
        var candidate = new SeedPeerCandidate(new CompactPubKey(Convert.FromHexString(NodeIdHex)),
                                              IPAddress.Parse("2001:db8::1"), 9735, "nodes.lightning.directory");

        // Act
        var info = candidate.ToPeerAddressInfo();

        // Assert
        Assert.Equal($"{NodeIdHex}@[2001:db8::1]:9735", info.Address);
    }
}