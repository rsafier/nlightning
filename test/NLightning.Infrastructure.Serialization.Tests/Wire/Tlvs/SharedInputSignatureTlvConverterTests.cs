namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class SharedInputSignatureTlvConverterTests
{
    private static readonly byte[] s_signature = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public void Given_SharedInputSignatureTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(0, s_signature);
        var expectedTlv = new SharedInputSignatureTlv(s_signature);
        var converter = new WireRegistry().GetTlvDefinition<SharedInputSignatureTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(s_signature, (byte[])tlv.Signature);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(72)]
    public void Given_WrongLength_When_ConvertFromBase_Then_ThrowsInvalidCastException(int length)
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<SharedInputSignatureTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(0, new byte[length])));
    }

    [Fact]
    public void Given_WrongType_When_ConvertFromBase_Then_ThrowsInvalidCastException()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<SharedInputSignatureTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(2, s_signature)));
    }
}