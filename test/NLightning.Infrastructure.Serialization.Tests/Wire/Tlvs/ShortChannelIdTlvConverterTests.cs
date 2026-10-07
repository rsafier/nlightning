using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class ShortChannelIdTlvConverterTests
{
    [Fact]
    public void Given_ShortChannelIdTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var shortChannelId = new ShortChannelId(1234, 5, 6);
        var expectedBaseTlv = new BaseTlv(1, shortChannelId);
        var expectedShortChannelIdTlv = new ShortChannelIdTlv(shortChannelId);
        var converter = new WireRegistry().GetTlvDefinition<ShortChannelIdTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedShortChannelIdTlv);
        var shortChannelIdTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedShortChannelIdTlv, shortChannelIdTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}