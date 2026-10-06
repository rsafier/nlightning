using NBitcoin;

namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class NextFundingTlvConverterTests
{
    [Fact]
    public void Given_NextFundingTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var nextFundingTxId = uint256.Zero.ToBytes();
        var expectedBaseTlv = new BaseTlv(1, [.. nextFundingTxId, 0x01]);
        var expectedNextFundingTlv = new NextFundingTlv(nextFundingTxId, 0x01);
        var converter = new WireRegistry().GetTlvDefinition<NextFundingTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedNextFundingTlv);
        var nextFundingTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedNextFundingTlv, nextFundingTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}