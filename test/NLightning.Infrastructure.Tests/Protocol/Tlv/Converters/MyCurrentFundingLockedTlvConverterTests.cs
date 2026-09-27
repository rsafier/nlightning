namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters;

using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters;

public class MyCurrentFundingLockedTlvConverterTests
{
    private static readonly byte[] s_txId = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData(MyCurrentFundingLockedTlv.AnnouncementSignaturesFlag)]
    public void Given_Tlv_When_ConvertingToBaseTlvAndBack_Then_RoundTrips(byte retransmitFlags)
    {
        // Arrange (channel_reestablish_tlvs type 5: [sha256:my_current_funding_locked_txid][byte:retransmit_flags])
        var expectedBaseTlv = new BaseTlv(5, [.. s_txId, retransmitFlags]);
        var expectedTlv = new MyCurrentFundingLockedTlv(new TxId(s_txId), retransmitFlags);
        var converter = new MyCurrentFundingLockedTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(s_txId, (byte[])tlv.FundingTxId);
        Assert.Equal(retransmitFlags, tlv.RetransmitFlags);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(34)]
    public void Given_WrongLength_When_ConvertFromBase_Then_Throws(int length)
    {
        // Arrange
        var converter = new MyCurrentFundingLockedTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(new BaseTlv(5, new byte[length])));
    }

    [Fact]
    public void Given_WrongType_When_ConvertFromBase_Then_Throws()
    {
        // Arrange
        var converter = new MyCurrentFundingLockedTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(new BaseTlv(1, new byte[33])));
    }
}