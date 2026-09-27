namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters;

public class StartBatchMessageTypeTlvConverterTests
{
    [Fact]
    public void Given_CommitmentSignedMessageType_When_ConvertingToBaseTlvAndBack_Then_RoundTrips()
    {
        // Arrange (start_batch_tlvs type 1: [u16:message_type], 132 = commitment_signed)
        var expectedBaseTlv = new BaseTlv(1, [0x00, 0x84]);
        var expectedTlv = StartBatchMessageTypeTlv.CommitmentSigned();
        var converter = new StartBatchMessageTypeTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal((ushort)132, tlv.MessageType);
    }

    [Fact]
    public void Given_OtherMessageType_When_ConvertFromBase_Then_ItIsKept()
    {
        // Arrange (the receiver, not the converter, ignores a start_batch whose message_type is not 132)
        var converter = new StartBatchMessageTypeTlvConverter();

        // Act
        var tlv = converter.ConvertFromBase(new BaseTlv(1, [0x01, 0x02]));

        // Assert
        Assert.Equal((ushort)0x0102, tlv.MessageType);
    }

    [Theory]
    [InlineData("84")]
    [InlineData("000084")]
    public void Given_WrongLength_When_ConvertFromBase_Then_Throws(string valueHex)
    {
        // Arrange
        var converter = new StartBatchMessageTypeTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() =>
                                                converter.ConvertFromBase(
                                                    new BaseTlv(1, Convert.FromHexString(valueHex))));
    }

    [Fact]
    public void Given_WrongType_When_ConvertFromBase_Then_Throws()
    {
        // Arrange
        var converter = new StartBatchMessageTypeTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(new BaseTlv(3, [0x00, 0x84])));
    }
}