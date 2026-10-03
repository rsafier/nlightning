namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters.Onion;

using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters.Onion;

public class TruncatedIntTlvConverterTests
{
    [Theory]
    [InlineData(0UL, "")]
    [InlineData(1000UL, "03e8")]
    [InlineData(ulong.MaxValue, "ffffffffffffffff")]
    public void Given_AmtToForwardTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(ulong msat, string hex)
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.AmtToForward, Convert.FromHexString(hex));
        var expectedTlv = new AmtToForwardTlv(LightningMoney.MilliSatoshis(msat));
        var converter = new AmtToForwardTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(msat, tlv.AmountToForward.MilliSatoshi);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("0003e8")]
    [InlineData("010000000000000000")]
    public void Given_NonMinimalOrTooLongAmount_When_ConvertingFromBase_Then_Throws(string hex)
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.AmtToForward, Convert.FromHexString(hex));
        var converter = new AmtToForwardTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingAmtToForward_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.OutgoingCltvValue, [0x01]);
        var converter = new AmtToForwardTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_LengthMismatch_When_ConvertingAmtToForward_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.AmtToForward, 2, [0x01]);
        var converter = new AmtToForwardTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Theory]
    [InlineData(0U, "")]
    [InlineData(800_000U, "0c3500")]
    [InlineData(uint.MaxValue, "ffffffff")]
    public void Given_OutgoingCltvValueTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(uint cltv, string hex)
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.OutgoingCltvValue, Convert.FromHexString(hex));
        var expectedTlv = new OutgoingCltvValueTlv(cltv);
        var converter = new OutgoingCltvValueTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(cltv, tlv.OutgoingCltvValue);
    }

    [Theory]
    [InlineData("000c3500")]
    [InlineData("0100000000")]
    public void Given_InvalidCltvEncoding_When_ConvertingFromBase_Then_Throws(string hex)
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.OutgoingCltvValue, Convert.FromHexString(hex));
        var converter = new OutgoingCltvValueTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingOutgoingCltvValue_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.AmtToForward, [0x01]);
        var converter = new OutgoingCltvValueTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Theory]
    [InlineData(0UL, "")]
    [InlineData(10_000UL, "2710")]
    public void Given_TotalAmountMsatTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(ulong msat, string hex)
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.TotalAmountMsat, Convert.FromHexString(hex));
        var expectedTlv = new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(msat));
        var converter = new TotalAmountMsatTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(msat, tlv.TotalAmount.MilliSatoshi);
    }

    [Fact]
    public void Given_WrongTypeOrNonMinimal_When_ConvertingTotalAmountMsat_Then_Throws()
    {
        // Arrange
        var wrongType = new BaseTlv(OnionPayloadTlvTypes.AmtToForward, [0x01]);
        var nonMinimal = new BaseTlv(OnionPayloadTlvTypes.TotalAmountMsat, [0x00, 0x01]);
        var converter = new TotalAmountMsatTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(wrongType));
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(nonMinimal));
    }
}