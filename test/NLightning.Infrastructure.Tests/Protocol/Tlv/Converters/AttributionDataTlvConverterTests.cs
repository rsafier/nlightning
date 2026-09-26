namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters;

public class AttributionDataTlvConverterTests
{
    private readonly AttributionDataTlvConverter _converter = new();

    [Fact]
    public void Given_AttributionDataTlv_When_ConvertingToBaseAndBack_Then_ValueIsKept()
    {
        // Arrange
        var value = Enumerable.Range(0, AttributionDataTlv.ValueLength).Select(i => (byte)(i % 251)).ToArray();
        var expectedBaseTlv = new BaseTlv(1, value);
        var expectedTlv = new AttributionDataTlv(value);

        // Act
        var baseTlv = _converter.ConvertToBase(expectedTlv);
        var tlv = _converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(value, tlv.AttributionData);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(919)]
    [InlineData(921)]
    public void Given_WrongLength_When_ConvertingFromBase_Then_Throws(int length)
    {
        // Arrange
        var baseTlv = new BaseTlv(1, new byte[length]);

        // Act / Assert
        Assert.Throws<InvalidCastException>(() => _converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingFromBase_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(3, new byte[AttributionDataTlv.ValueLength]);

        // Act / Assert
        Assert.Throws<InvalidCastException>(() => _converter.ConvertFromBase(baseTlv));
    }
}