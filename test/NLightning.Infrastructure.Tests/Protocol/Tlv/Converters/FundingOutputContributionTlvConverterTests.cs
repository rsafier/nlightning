namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Money;
using Domain.Protocol.Tlv;
using Infrastructure.Converters;
using Infrastructure.Protocol.Tlv.Converters;

public class FundingOutputContributionTlvConverterTests
{
    [Fact]
    public void Given_FundingOutputContributionTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var amount = LightningMoney.Satoshis(100_000);
        var expectedBaseTlv = new BaseTlv(0, EndianBitConverter.GetBytesBigEndian(amount.Satoshi));
        var expectedFundingOutputContributionTlv = new FundingOutputContributionTlv(amount);
        var converter = new FundingOutputContributionTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedFundingOutputContributionTlv);
        var fundingOutputContributionTlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedFundingOutputContributionTlv, fundingOutputContributionTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }

    [Theory]
    [InlineData(-50_000L, "FFFFFFFFFFFF3CB0")]
    [InlineData(-1L, "FFFFFFFFFFFFFFFF")]
    [InlineData(long.MinValue, "8000000000000000")]
    [InlineData(long.MaxValue, "7FFFFFFFFFFFFFFF")]
    [InlineData(10L, "000000000000000A")]
    public void Given_SignedContribution_When_ConvertingToBaseTlvAndBack_Then_S64RoundTrips(long satoshis,
        string expectedValueHex)
    {
        // Arrange (BOLT 2 tx_init_rbf/tx_ack_rbf: funding_output_contribution is [s64:satoshis], negative for a
        // splice-out RBF)
        var converter = new FundingOutputContributionTlvConverter();
        var tlv = new FundingOutputContributionTlv(satoshis);

        // Act
        var baseTlv = converter.ConvertToBase(tlv);
        var roundTripped = converter.ConvertFromBase(new BaseTlv(0, Convert.FromHexString(expectedValueHex)));

        // Assert
        Assert.Equal(expectedValueHex, Convert.ToHexString(baseTlv.Value));
        Assert.Equal(expectedValueHex, Convert.ToHexString(tlv.Value));
        Assert.Equal(satoshis, roundTripped.Satoshis);
        Assert.Equal(tlv, roundTripped);
    }

    [Fact]
    public void Given_LightningMoneyContribution_When_Created_Then_ItIsWholeSatoshis()
    {
        // Arrange & Act (the old LightningMoney constructor keeps its meaning)
        var tlv = new FundingOutputContributionTlv(LightningMoney.Satoshis(10));

        // Assert
        Assert.Equal(10L, tlv.Satoshis);
    }

    [Theory]
    [InlineData("00000000000000")]
    [InlineData("000000000000000000")]
    public void Given_WrongLength_When_ConvertFromBase_Then_Throws(string valueHex)
    {
        // Arrange
        var converter = new FundingOutputContributionTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() =>
                                                converter.ConvertFromBase(
                                                    new BaseTlv(0, Convert.FromHexString(valueHex))));
    }

    [Fact]
    public void Given_WrongType_When_ConvertFromBase_Then_Throws()
    {
        // Arrange
        var converter = new FundingOutputContributionTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(new BaseTlv(2, new byte[8])));
    }
}