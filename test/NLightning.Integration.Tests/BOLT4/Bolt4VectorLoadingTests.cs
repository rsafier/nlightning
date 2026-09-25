using System.Text.Json;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

public class Bolt4VectorLoadingTests
{
    private const int OnionPacketLength = 1366;
    private const int ErrorPacketLength = 292;

    [Fact]
    public void Given_OnionTestFile_When_Loading_Then_HasFiveHopsAndSharedConstants()
    {
        // Arrange & Act
        var vector = Bolt4Vectors.LoadOnionTest();

        // Assert
        Assert.Equal(5, vector.Hops.Count);
        Assert.Equal(5, vector.DecodePrivateKeys.Count);
        Assert.Equal(Bolt4Vectors.SessionKey, vector.SessionKey);
        Assert.Equal(Bolt4Vectors.AssociatedData, vector.AssociatedData);
        Assert.Equal(OnionPacketLength, vector.Onion.Length);
        Assert.All(vector.Hops, hop => Assert.Equal(33, hop.PubKey.Length));
        Assert.All(vector.DecodePrivateKeys, key => Assert.Equal(32, key.Length));
    }

    [Fact]
    public void Given_OnionErrorTestFile_When_Loading_Then_HasFiveHopsAndOnlyLastHopHasUmKey()
    {
        // Arrange & Act
        var vector = Bolt4Vectors.LoadOnionErrorTest();

        // Assert
        Assert.Equal(5, vector.Hops.Count);
        Assert.Equal(Bolt4Vectors.SessionKey, vector.SessionKey);
        Assert.Equal(ErrorPacketLength, vector.ErrorPacket.Length);
        Assert.All(vector.Hops, hop =>
        {
            Assert.Equal(32, hop.SharedSecret.Length);
            Assert.Equal(32, hop.AmmagKey.Length);
        });
        Assert.All(vector.Hops.Take(4), hop => Assert.Null(hop.UmKey));
        Assert.NotNull(vector.Hops[4].UmKey);
        Assert.NotNull(vector.Hops[4].Payload);
    }

    [Theory]
    [InlineData(Bolt4Vectors.RouteBlindingTestPath, "generate.hops", 4)]
    [InlineData(Bolt4Vectors.RouteBlindingTestPath, "route.hops", 4)]
    [InlineData(Bolt4Vectors.RouteBlindingTestPath, "unblind.hops", 4)]
    [InlineData(Bolt4Vectors.BlindedPaymentOnionTestPath, "generate.blinded_route.hops", 4)]
    [InlineData(Bolt4Vectors.BlindedPaymentOnionTestPath, "generate.full_route.hops", 5)]
    [InlineData(Bolt4Vectors.BlindedPaymentOnionTestPath, "decrypt.hops", 5)]
    [InlineData(Bolt4Vectors.BlindedOnionMessageOnionTestPath, "generate.hops", 4)]
    [InlineData(Bolt4Vectors.BlindedOnionMessageOnionTestPath, "route.hops", 4)]
    [InlineData(Bolt4Vectors.BlindedOnionMessageOnionTestPath, "decrypt.hops", 4)]
    public void Given_BlindingTestFile_When_Loading_Then_HopCountMatches(string path, string hopsPath,
                                                                         int expectedHopCount)
    {
        // Arrange
        using var document = Bolt4Vectors.LoadDocument(path);

        // Act
        var hops = hopsPath.Split('.')
                           .Aggregate(document.RootElement, Bolt4Vectors.GetRequired);

        // Assert
        Assert.Equal(JsonValueKind.Array, hops.ValueKind);
        Assert.Equal(expectedHopCount, hops.GetArrayLength());
    }
}