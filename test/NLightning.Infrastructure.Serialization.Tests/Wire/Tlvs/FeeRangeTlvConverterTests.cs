namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Money;
using Domain.Protocol.Tlv;
using Infrastructure.Converters;
using NLightning.Infrastructure.Serialization.Wire;

public class FeeRangeTlvConverterTests
{
    [Fact]
    public void Given_FeeRangeTlvConverter_When_ConvertingToBaseTlvAndBack_ResultIsCorrect()
    {
        // Arrange
        var minFeeAmount = LightningMoney.Satoshis(1);
        var maxFeeAmount = LightningMoney.Satoshis(2);
        var tlvValue = new byte[sizeof(ulong) * 2];
        EndianBitConverter.GetBytesBigEndian(minFeeAmount.Satoshi).CopyTo(tlvValue, 0);
        EndianBitConverter.GetBytesBigEndian(maxFeeAmount.Satoshi).CopyTo(tlvValue, sizeof(ulong));
        var expectedBaseTlv = new BaseTlv(1, tlvValue);
        var expectedFeeRangeTlv = new FeeRangeTlv(minFeeAmount, maxFeeAmount);
        var converter = new WireRegistry().GetTlvDefinition<FeeRangeTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedFeeRangeTlv);
        var feeRangeTlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedFeeRangeTlv, feeRangeTlv);
        Assert.Equal(expectedBaseTlv, baseTlv);
    }
}