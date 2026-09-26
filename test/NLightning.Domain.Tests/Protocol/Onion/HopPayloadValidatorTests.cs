namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class HopPayloadValidatorTests
{
    private static readonly byte[] s_pathKey =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    private static AmtToForwardTlv Amt => new(LightningMoney.MilliSatoshis(10_000));
    private static OutgoingCltvValueTlv Cltv => new(800_000);
    private static OnionShortChannelIdTlv Scid => new(new ShortChannelId(700_000, 1, 0));

    private static PaymentDataTlv PaymentData =>
        new(new Secret(Enumerable.Repeat((byte)0x24, 32).ToArray()), LightningMoney.MilliSatoshis(10_000));

    private static EncryptedRecipientDataTlv EncryptedData => new([0xde, 0xad, 0xbe, 0xef]);
    private static CurrentPathKeyTlv PathKey => new(new CompactPubKey(s_pathKey));
    private static TotalAmountMsatTlv TotalAmount => new(LightningMoney.MilliSatoshis(10_000));

    #region Non-blinded

    [Fact]
    public void Given_ValidIntermediatePayload_When_Validating_Then_Succeeds()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, false, false, out var error);

        // Assert
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void Given_ValidFinalPayload_When_Validating_Then_DoesNotThrow()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, PaymentData, new PaymentMetadataTlv([0x01]));

        // Act
        var exception = Record.Exception(() => HopPayloadValidator.Validate(payload, true, false));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_IntermediatePayloadWithUnknownOddTlv_When_Validating_Then_Succeeds()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid, new BaseTlv(new BigSize(513), [0x01]));

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(payload, false, false, out _));
    }

    [Fact]
    public void Given_IntermediatePayloadWithoutScid_When_Validating_Then_FailsWithType6()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.ShortChannelId, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_PayloadWithoutAmtToForward_When_Validating_Then_FailsWithType2(bool isFinalHop)
    {
        // Arrange
        var payload = isFinalHop ? new HopPayload(Cltv, PaymentData) : new HopPayload(Cltv, Scid);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, isFinalHop, false, out var error);

        // Assert
        Assert.False(isValid);
        AssertInvalidOnionPayload(error, OnionPayloadTlvTypes.AmtToForward, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_PayloadWithoutOutgoingCltv_When_Validating_Then_FailsWithType4(bool isFinalHop)
    {
        // Arrange
        var payload = isFinalHop ? new HopPayload(Amt, PaymentData) : new HopPayload(Amt, Scid);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, isFinalHop, false, out var error);

        // Assert
        Assert.False(isValid);
        AssertInvalidOnionPayload(error, OnionPayloadTlvTypes.OutgoingCltvValue, 0);
    }

    [Fact]
    public void Given_FinalPayloadWithScid_When_Validating_Then_Succeeds()
    {
        // Arrange: "MUST NOT include short_channel_id for the final node" is a writer rule; the reader ignores it
        var payload = new HopPayload(Amt, Cltv, Scid, PaymentData);

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(payload, true, false, out _));
    }

    [Fact]
    public void Given_FinalNonBlindedPayloadWithoutPaymentData_When_Validating_Then_FailsWithType8()
    {
        // Arrange (BOLT 4: the final node MUST return an error if total_msat is not present)
        var payload = new HopPayload(Amt, Cltv);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.PaymentData, 0);
    }

    [Fact]
    public void Given_NonBlindedPayloadWithCurrentPathKey_When_Validating_Then_FailsWithType12()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid, PathKey);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.CurrentPathKey, 0);
    }

    [Fact]
    public void Given_NonBlindedPayloadAndUpdateAddPathKey_When_Validating_Then_FailsWithType10()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.EncryptedRecipientData, 0);
    }

    #endregion

    #region Blinded

    [Fact]
    public void Given_BlindedIntermediatePayloadWithCurrentPathKey_When_Validating_Then_Succeeds()
    {
        // Arrange
        var payload = new HopPayload(EncryptedData, PathKey);

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(payload, false, false, out _));
    }

    [Fact]
    public void Given_BlindedIntermediatePayloadAndUpdateAddPathKey_When_Validating_Then_Succeeds()
    {
        // Arrange
        var payload = new HopPayload(EncryptedData);

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(payload, false, true, out _));
    }

    [Fact]
    public void Given_BlindedPayloadWithBothPathKeys_When_Validating_Then_FailsWithType12()
    {
        // Arrange
        var payload = new HopPayload(EncryptedData, PathKey);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.CurrentPathKey, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_BlindedPayloadWithNeitherPathKey_When_Validating_Then_FailsWithType12(bool isFinalHop)
    {
        // Arrange
        var payload = isFinalHop
                          ? new HopPayload(Amt, Cltv, EncryptedData, TotalAmount)
                          : new HopPayload(EncryptedData);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, isFinalHop, false, out var error);

        // Assert
        Assert.False(isValid);
        AssertInvalidOnionPayload(error, OnionPayloadTlvTypes.CurrentPathKey, 0);
    }

    [Theory]
    [InlineData(2UL)]
    [InlineData(4UL)]
    [InlineData(6UL)]
    [InlineData(8UL)]
    [InlineData(16UL)]
    [InlineData(18UL)]
    public void Given_BlindedIntermediatePayloadWithOtherKnownTlv_When_Validating_Then_FailsWithThatType(
        ulong extraType)
    {
        // Arrange
        BaseTlv extra = extraType switch
        {
            2 => Amt,
            4 => Cltv,
            6 => Scid,
            8 => PaymentData,
            16 => new PaymentMetadataTlv([0x01]),
            _ => TotalAmount
        };
        var payload = new HopPayload(EncryptedData, extra);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(extraType), 0);
    }

    [Theory]
    [InlineData(301UL)]
    [InlineData(65537UL)]
    public void Given_BlindedIntermediatePayloadWithUnknownOddTlv_When_Validating_Then_FailsWithThatTypeAndOffset(
        ulong unknownType)
    {
        // Arrange: BOLT 4 allows only encrypted_recipient_data and current_path_key, with no unknown-odd exception
        var tlvStream = new TlvStream();
        tlvStream.Add(EncryptedData, new BaseTlv(new BigSize(unknownType), [0x2a]));
        var offsets = new Dictionary<BigSize, int>
        {
            [OnionPayloadTlvTypes.EncryptedRecipientData] = 1,
            [new BigSize(unknownType)] = 40
        };
        var payload = new HopPayload(tlvStream, offsets);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(unknownType), 40);
    }

    [Fact]
    public void Given_BlindedFinalPayloadWithUnknownOddTlv_When_Validating_Then_FailsWithThatType()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, EncryptedData, PathKey, TotalAmount,
                                     new BaseTlv(new BigSize(65537), [0x01]));

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(65537), 0);
    }

    [Fact]
    public void Given_ValidBlindedFinalPayload_When_Validating_Then_Succeeds()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, EncryptedData, PathKey, TotalAmount);

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(payload, true, false, out _));
    }

    [Theory]
    [InlineData(6UL)]
    [InlineData(8UL)]
    [InlineData(16UL)]
    public void Given_BlindedFinalPayloadWithForbiddenTlv_When_Validating_Then_FailsWithThatType(ulong extraType)
    {
        // Arrange
        BaseTlv extra = extraType switch
        {
            6 => Scid,
            8 => PaymentData,
            _ => new PaymentMetadataTlv([0x01])
        };
        var payload = new HopPayload(Amt, Cltv, EncryptedData, TotalAmount, extra);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, true));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(extraType), 0);
    }

    [Theory]
    [InlineData(2UL)]
    [InlineData(4UL)]
    [InlineData(18UL)]
    public void Given_BlindedFinalPayloadMissingRequiredTlv_When_Validating_Then_FailsWithThatType(
        ulong missingType)
    {
        // Arrange
        var tlvs = new List<BaseTlv> { Amt, Cltv, EncryptedData, TotalAmount }
                  .Where(tlv => tlv.Type.Value != missingType)
                  .ToArray();
        var payload = new HopPayload(tlvs);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, true));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(missingType), 0);
    }

    #endregion

    [Fact]
    public void Given_HandBuiltPayloadWithUnknownEvenTlv_When_Validating_Then_FailsWithThatTypeAndOffset()
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(Amt, Cltv, Scid, new BaseTlv(new BigSize(20), [0x01]));
        var offsets = new Dictionary<BigSize, int> { [new BigSize(20)] = 19 };
        var payload = new HopPayload(tlvStream, offsets);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(20), 19);
    }

    [Fact]
    public void Given_NullPayload_When_Validating_Then_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => HopPayloadValidator.Validate(null!, false, false));
    }

    private static void AssertInvalidOnionPayload(OnionException? exception, BigSize expectedType,
                                                  ushort expectedOffset)
    {
        Assert.NotNull(exception);
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.NotNull(exception.FailureData);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData.Value.Span,
                                                                    out var type, out var offset));
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedOffset, offset);
    }
}