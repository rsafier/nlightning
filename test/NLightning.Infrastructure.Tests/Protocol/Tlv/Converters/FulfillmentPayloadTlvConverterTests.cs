namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters;

public class FulfillmentPayloadTlvConverterTests
{
    private readonly FulfillmentPayloadTlvConverter _converter = new();

    [Theory]
    [InlineData(0)]
    [InlineData(272)]
    [InlineData(32769)]
    public void Given_FulfillmentPayloadTlv_When_ConvertingToBaseAndBack_Then_ValueIsKept(int length)
    {
        // Arrange
        var value = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var expectedBaseTlv = new BaseTlv(3, value);
        var expectedTlv = new FulfillmentPayloadTlv(value);

        // Act
        var baseTlv = _converter.ConvertToBase(expectedTlv);
        var tlv = _converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(length > 32768, tlv.IsTooLong);
    }

    [Fact]
    public void Given_WrongType_When_ConvertingFromBase_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(1, [0x01]);

        // Act / Assert
        Assert.Throws<InvalidCastException>(() => _converter.ConvertFromBase(baseTlv));
    }
}