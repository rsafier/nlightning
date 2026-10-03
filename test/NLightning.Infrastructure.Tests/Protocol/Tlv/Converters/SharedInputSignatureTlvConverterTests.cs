namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters;

public class SharedInputSignatureTlvConverterTests
{
    private static readonly byte[] s_signature = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public void Given_SharedInputSignatureTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(0, s_signature);
        var expectedTlv = new SharedInputSignatureTlv(s_signature);
        var converter = new SharedInputSignatureTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

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
        var converter = new SharedInputSignatureTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(new BaseTlv(0, new byte[length])));
    }

    [Fact]
    public void Given_WrongType_When_ConvertFromBase_Then_ThrowsInvalidCastException()
    {
        // Arrange
        var converter = new SharedInputSignatureTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(new BaseTlv(2, s_signature)));
    }
}