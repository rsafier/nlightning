namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class PrevTxDetailsTlvConverterTests
{
    private static readonly byte[] s_txId = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    private static readonly byte[] s_p2Tr = [0x51, 0x20, .. Enumerable.Repeat((byte)0x55, 32)];

    // txid || u64 BE 100,000 sat (0x186A0) || P2TR script, as Eclair's PrevTxOut codec writes it
    private static readonly byte[] s_value = [.. s_txId, 0, 0, 0, 0, 0, 0x01, 0x86, 0xA0, .. s_p2Tr];

    [Theory]
    [InlineData(2UL)]
    [InlineData(1111UL)]
    public void Given_PrevTxDetailsTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect(ulong type)
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(type, s_value);
        var expectedTlv = new PrevTxDetailsTlv(s_txId, 100_000, s_p2Tr, type);
        var converter = new WireRegistry().GetTlvDefinition<PrevTxDetailsTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(type, (ulong)tlv.Type);
        Assert.Equal(s_txId, (byte[])tlv.PrevTxId);
        Assert.Equal(100_000UL, tlv.AmountSatoshis);
        Assert.Equal(new BitcoinScript(s_p2Tr), tlv.ScriptPubKey);
    }

    [Fact]
    public void Given_NoType_When_Constructing_Then_TheSpecTypeIsUsed()
    {
        // Act
        var tlv = new PrevTxDetailsTlv(s_txId, 100_000, s_p2Tr);

        // Assert
        Assert.Equal(InteractiveTxTlvConstants.PrevTxDetails, tlv.Type);
        Assert.Equal(s_value, tlv.Value);
    }

    [Fact]
    public void Given_AnEmptyScript_When_ConvertFromBase_Then_ItIsRead()
    {
        // Arrange: the script is "...*byte"; the interactive-tx rules refuse an empty one, not the codec
        var converter = new WireRegistry().GetTlvDefinition<PrevTxDetailsTlv>()!;

        // Act
        var tlv = converter.Decode(new BaseTlv(2, s_value[..PrevTxDetailsTlv.FixedLength]));

        // Assert
        Assert.Equal(0, tlv.ScriptPubKey.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(39)]
    public void Given_ShorterThanTxIdAndAmount_When_ConvertFromBase_Then_ThrowsInvalidCastException(int length)
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<PrevTxDetailsTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(2, new byte[length])));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(1105UL)]
    public void Given_WrongType_When_ConvertFromBase_Then_ThrowsInvalidCastException(ulong type)
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<PrevTxDetailsTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(type, s_value)));
    }

    [Fact]
    public void Given_WrongType_When_Constructing_Then_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new PrevTxDetailsTlv(s_txId, 1, s_p2Tr, 4));
    }
}