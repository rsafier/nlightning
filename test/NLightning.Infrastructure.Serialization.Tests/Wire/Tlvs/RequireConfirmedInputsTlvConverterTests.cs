namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class RequireConfirmedInputsTlvConverterTests
{
    [Fact]
    public void Given_RequireConfirmedInputsTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(2, []);
        var expectedRequireConfirmedInputsTlv = new RequireConfirmedInputsTlv();
        var converter = new WireRegistry().GetTlvDefinition<RequireConfirmedInputsTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedRequireConfirmedInputsTlv);
        var requireConfirmedInputsTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedRequireConfirmedInputsTlv, requireConfirmedInputsTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}