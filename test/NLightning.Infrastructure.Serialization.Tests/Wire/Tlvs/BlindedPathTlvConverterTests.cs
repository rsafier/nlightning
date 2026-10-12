using NBitcoin;

namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class BlindedPathTlvConverterTests
{
    [Fact]
    public void Given_BlindedPathTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var pubkey = new Key().PubKey.ToBytes();
        var expectedBaseTlv = new BaseTlv(0, pubkey);
        var expectedBlindedPathTlv = new BlindedPathTlv(pubkey);
        var converter = new WireRegistry().GetTlvDefinition<BlindedPathTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedBlindedPathTlv);
        var blindedPathTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBlindedPathTlv, blindedPathTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(34)]
    public void Given_WrongLength_When_ConvertFromBase_Then_ThrowsInvalidCastException(int length)
    {
        // Arrange
        var value = new byte[length];
        value[0] = 0x02;
        var baseTlv = new BaseTlv(0, value);
        var converter = new WireRegistry().GetTlvDefinition<BlindedPathTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    [Fact]
    public void Given_InvalidPointPrefix_When_ConvertFromBase_Then_ThrowsInvalidCastException()
    {
        // Arrange
        var value = new Key().PubKey.ToBytes();
        value[0] = 0x04;
        var baseTlv = new BaseTlv(0, value);
        var converter = new WireRegistry().GetTlvDefinition<BlindedPathTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }
}