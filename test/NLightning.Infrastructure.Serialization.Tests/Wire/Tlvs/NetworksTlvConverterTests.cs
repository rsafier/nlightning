using NLightning.Domain.Protocol.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class NetworksTlvConverterTests
{
    [Fact]
    public void Given_NetworksTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var chainHash = BitcoinNetwork.Mainnet.ChainHash;
        var expectedBaseTlv = new BaseTlv(1, chainHash);
        var expectedNetworksTlv = new NetworksTlv([chainHash]);
        var converter = new WireRegistry().GetTlvDefinition<NetworksTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedNetworksTlv);
        var networksTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedNetworksTlv, networksTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}