using NLightning.Domain.Bitcoin.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class UpfrontShutdownScriptTlvConverterTests
{
    [Fact]
    public void Given_UpfrontShutdownScriptTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var script = new BitcoinScript([0x01, 0x02, 0x03]);
        var expectedBaseTlv = new BaseTlv(0, script);
        var expectedUpfrontShutdownScriptTlv = new UpfrontShutdownScriptTlv(script);
        var converter = new WireRegistry().GetTlvDefinition<UpfrontShutdownScriptTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedUpfrontShutdownScriptTlv);
        var upfrontShutdownScriptTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedUpfrontShutdownScriptTlv, upfrontShutdownScriptTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}