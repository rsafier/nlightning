namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters.Onion;

public class PaymentDataTlvConverterTests
{
    private static readonly byte[] s_secret = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    [Theory]
    [InlineData(0UL, "")]
    [InlineData(100_000UL, "0186a0")]
    [InlineData(ulong.MaxValue, "ffffffffffffffff")]
    public void Given_PaymentDataTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(ulong totalMsat,
        string totalHex)
    {
        // Arrange
        var expectedValue = s_secret.Concat(Convert.FromHexString(totalHex)).ToArray();
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.PaymentData, expectedValue);
        var expectedTlv = new PaymentDataTlv(new Secret(s_secret.ToArray()), LightningMoney.MilliSatoshis(totalMsat));
        var converter = new PaymentDataTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(new Secret(s_secret), tlv.PaymentSecret);
        Assert.Equal(totalMsat, tlv.TotalMsat.MilliSatoshi);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(41)]
    public void Given_WrongLength_When_ConvertingFromBase_Then_Throws(int length)
    {
        // Arrange
        var value = Enumerable.Repeat((byte)0x01, length).ToArray();
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.PaymentData, value);
        var converter = new PaymentDataTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_NonMinimalTotalMsat_When_ConvertingFromBase_Then_Throws()
    {
        // Arrange
        var value = s_secret.Concat(new byte[] { 0x00, 0x01 }).ToArray();
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.PaymentData, value);
        var converter = new PaymentDataTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingFromBase_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.PaymentMetadata, s_secret.ToArray());
        var converter = new PaymentDataTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }
}