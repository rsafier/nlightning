namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class SharedInputTxIdTlvConverterTests
{
    private static readonly byte[] s_txId = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public void Given_SharedInputTxIdTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(0, s_txId);
        var expectedTlv = new SharedInputTxIdTlv(s_txId);
        var converter = new WireRegistry().GetTlvDefinition<SharedInputTxIdTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(s_txId, (byte[])tlv.FundingTxId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_WrongLength_When_ConvertFromBase_Then_ThrowsInvalidCastException(int length)
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<SharedInputTxIdTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(0, new byte[length])));
    }

    [Fact]
    public void Given_WrongType_When_ConvertFromBase_Then_ThrowsInvalidCastException()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<SharedInputTxIdTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(1, s_txId)));
    }
}