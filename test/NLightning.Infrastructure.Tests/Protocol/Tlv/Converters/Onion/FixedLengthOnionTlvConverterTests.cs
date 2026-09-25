namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Infrastructure.Protocol.Tlv.Converters.Onion;

public class FixedLengthOnionTlvConverterTests
{
    private static readonly byte[] s_pubKey =
        Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");

    private static readonly byte[] s_scid = Convert.FromHexString("0aae600004d20001");

    [Fact]
    public void Given_OnionShortChannelIdTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.ShortChannelId, s_scid.ToArray());
        var expectedTlv = new OnionShortChannelIdTlv(new ShortChannelId(700_000, 1234, 1));
        var converter = new OnionShortChannelIdTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(700_000U, tlv.ShortChannelId.BlockHeight);
        Assert.Equal(1234U, tlv.ShortChannelId.TransactionIndex);
        Assert.Equal((ushort)1, tlv.ShortChannelId.OutputIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void Given_WrongLength_When_ConvertingShortChannelId_Then_Throws(int length)
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.ShortChannelId, new byte[length]);
        var converter = new OnionShortChannelIdTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingShortChannelId_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(EncryptedDataTlvTypes.ShortChannelId, s_scid.ToArray());
        var converter = new OnionShortChannelIdTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_CurrentPathKeyTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.CurrentPathKey, s_pubKey.ToArray());
        var expectedTlv = new CurrentPathKeyTlv(new CompactPubKey(s_pubKey.ToArray()));
        var converter = new CurrentPathKeyTlvConverter();

        // Act
        var baseTlv = converter.ConvertToBase(expectedTlv);
        var tlv = converter.ConvertFromBase(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(new CompactPubKey(s_pubKey), tlv.PathKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(34)]
    public void Given_WrongLength_When_ConvertingCurrentPathKey_Then_Throws(int length)
    {
        // Arrange
        var value = new byte[length];
        if (length > 0)
            value[0] = 0x02;
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.CurrentPathKey, value);
        var converter = new CurrentPathKeyTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_InvalidPointPrefix_When_ConvertingCurrentPathKey_Then_Throws()
    {
        // Arrange
        var value = s_pubKey.ToArray();
        value[0] = 0x04;
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.CurrentPathKey, value);
        var converter = new CurrentPathKeyTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingCurrentPathKey_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(EncryptedDataTlvTypes.NextPathKeyOverride, s_pubKey.ToArray());
        var converter = new CurrentPathKeyTlvConverter();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.ConvertFromBase(baseTlv));
    }
}