namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class ChannelTypeTlvConverterTests
{
    [Fact]
    public void Given_ChannelTypeTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        byte[] channelType = [0x01, 0x02, 0x03];
        var expectedBaseTlv = new BaseTlv(1, channelType);
        var expectedChannelTypeTlv = new ChannelTypeTlv(channelType);
        var converter = new WireRegistry().GetTlvDefinition<ChannelTypeTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedChannelTypeTlv);
        var channelTypeTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedChannelTypeTlv, channelTypeTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}