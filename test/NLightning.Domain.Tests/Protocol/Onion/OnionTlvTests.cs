namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;

public class OnionTlvTests
{
    [Theory]
    [InlineData(0UL, "")]
    [InlineData(1UL, "01")]
    [InlineData(0xFFUL, "ff")]
    [InlineData(0x100UL, "0100")]
    [InlineData(0x1000000UL, "01000000")]
    [InlineData(ulong.MaxValue, "ffffffffffffffff")]
    public void Given_Amount_When_CreatingAmtToForwardTlv_Then_ValueIsTruncated(ulong msat, string expectedHex)
    {
        // Act
        var tlv = new AmtToForwardTlv(LightningMoney.MilliSatoshis(msat));

        // Assert
        Assert.Equal(OnionPayloadTlvTypes.AmtToForward, tlv.Type);
        Assert.Equal(Convert.FromHexString(expectedHex), tlv.Value);
        Assert.Equal((ulong)tlv.Value.Length, tlv.Length.Value);
        Assert.Equal(msat, tlv.AmountToForward.MilliSatoshi);
    }

    [Theory]
    [InlineData(0U, "")]
    [InlineData(144U, "90")]
    [InlineData(800_000U, "0c3500")]
    [InlineData(uint.MaxValue, "ffffffff")]
    public void Given_Cltv_When_CreatingOutgoingCltvValueTlv_Then_ValueIsTruncated(uint cltv, string expectedHex)
    {
        // Act
        var tlv = new OutgoingCltvValueTlv(cltv);

        // Assert
        Assert.Equal(OnionPayloadTlvTypes.OutgoingCltvValue, tlv.Type);
        Assert.Equal(Convert.FromHexString(expectedHex), tlv.Value);
        Assert.Equal(cltv, tlv.OutgoingCltvValue);
    }

    [Fact]
    public void Given_Amount_When_CreatingTotalAmountMsatTlv_Then_TypeIs18AndValueIsTruncated()
    {
        // Act
        var tlv = new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(10_000UL));

        // Assert
        Assert.Equal(18UL, tlv.Type.Value);
        Assert.Equal(Convert.FromHexString("2710"), tlv.Value);
    }

    [Fact]
    public void Given_SecretAndTotal_When_CreatingPaymentDataTlv_Then_ValueIsSecretFollowedByTu64()
    {
        // Arrange
        var secret = Enumerable.Repeat((byte)0x42, 32).ToArray();

        // Act
        var tlv = new PaymentDataTlv(new Secret(secret), LightningMoney.MilliSatoshis(0x0186A0UL));

        // Assert
        Assert.Equal(8UL, tlv.Type.Value);
        Assert.Equal(35UL, tlv.Length.Value);
        Assert.Equal(secret, tlv.Value[..32]);
        Assert.Equal(Convert.FromHexString("0186a0"), tlv.Value[32..]);
    }

    [Fact]
    public void Given_ZeroTotal_When_CreatingPaymentDataTlv_Then_ValueIsOnlySecret()
    {
        // Act
        var tlv = new PaymentDataTlv(new Secret(new byte[32]), LightningMoney.Zero);

        // Assert
        Assert.Equal(32, tlv.Value.Length);
    }

    [Fact]
    public void Given_SecretLongerThan32Bytes_When_CreatingPaymentDataTlv_Then_Throws()
    {
        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => new PaymentDataTlv(new Secret(new byte[33]), LightningMoney.Zero));
    }

    [Fact]
    public void Given_ShortChannelId_When_CreatingOnionShortChannelIdTlv_Then_ValueIs8Bytes()
    {
        // Arrange
        var scid = new ShortChannelId(700_000, 1234, 1);

        // Act
        var tlv = new OnionShortChannelIdTlv(scid);

        // Assert
        Assert.Equal(6UL, tlv.Type.Value);
        Assert.Equal(Convert.FromHexString("0aae600004d20001"), tlv.Value);
    }

    [Fact]
    public void Given_PathKey_When_CreatingCurrentPathKeyTlv_Then_ValueIsPoint()
    {
        // Arrange
        var key = Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");

        // Act
        var tlv = new CurrentPathKeyTlv(new CompactPubKey(key));

        // Assert
        Assert.Equal(12UL, tlv.Type.Value);
        Assert.Equal(key, tlv.Value);
    }

    [Fact]
    public void Given_Bytes_When_CreatingVariableLengthTlvs_Then_ValuesAreCopied()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };

        // Act
        var encrypted = new EncryptedRecipientDataTlv(data);
        var metadata = new PaymentMetadataTlv(data);
        data[0] = 0xFF;

        // Assert
        Assert.Equal(10UL, encrypted.Type.Value);
        Assert.Equal(16UL, metadata.Type.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, encrypted.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, metadata.PaymentMetadata.ToArray());
        Assert.Equal(3UL, encrypted.Length.Value);
    }
}