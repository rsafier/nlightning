namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs.Onion;

using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class VariableLengthOnionTlvConverterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0102030405060708090a0b0c0d0e0f101112")]
    public void Given_EncryptedRecipientDataTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(string hex)
    {
        // Arrange
        var data = Convert.FromHexString(hex);
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.EncryptedRecipientData, data.ToArray());
        var expectedTlv = new EncryptedRecipientDataTlv(data);
        var converter = new WireRegistry().GetTlvDefinition<EncryptedRecipientDataTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(data, tlv.EncryptedRecipientData.ToArray());
    }

    [Fact]
    public void Given_WrongTypeOrLength_When_ConvertingEncryptedRecipientData_Then_Throws()
    {
        // Arrange
        var wrongType = new BaseTlv(OnionPayloadTlvTypes.PaymentMetadata, [0x01]);
        var wrongLength = new BaseTlv(OnionPayloadTlvTypes.EncryptedRecipientData, 2, [0x01]);
        var converter = new WireRegistry().GetTlvDefinition<EncryptedRecipientDataTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(wrongType));
        Assert.Throws<InvalidCastException>(() => converter.Decode(wrongLength));
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]
    public void Given_PaymentMetadataTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(string hex)
    {
        // Arrange
        var data = Convert.FromHexString(hex);
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.PaymentMetadata, data.ToArray());
        var expectedTlv = new PaymentMetadataTlv(data);
        var converter = new WireRegistry().GetTlvDefinition<PaymentMetadataTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(data, tlv.PaymentMetadata.ToArray());
    }

    [Fact]
    public void Given_WrongTypeOrLength_When_ConvertingPaymentMetadata_Then_Throws()
    {
        // Arrange
        var wrongType = new BaseTlv(OnionPayloadTlvTypes.EncryptedRecipientData, [0x01]);
        var wrongLength = new BaseTlv(OnionPayloadTlvTypes.PaymentMetadata, 0, [0x01]);
        var converter = new WireRegistry().GetTlvDefinition<PaymentMetadataTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(wrongType));
        Assert.Throws<InvalidCastException>(() => converter.Decode(wrongLength));
    }
}