using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Codecs;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// <see cref="HopPayloadValidator"/> with the trampoline records (BOLTs PR 836): unchanged without the opt-in, the
/// outer payload rules of a trampoline node with it.
/// </summary>
public class HopPayloadValidatorTrampolineTests
{
    private static readonly byte[] s_nodeId =
        Convert.FromHexString("02edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145");

    private static AmtToForwardTlv Amt => new(LightningMoney.MilliSatoshis(100_005_000));
    private static OutgoingCltvValueTlv Cltv => new(800_250);
    private static OnionShortChannelIdTlv Scid => new(new ShortChannelId(572_330, 7, 1105));

    private static PaymentDataTlv PaymentData =>
        new(new Secret(Enumerable.Repeat((byte)0x2b, 32).ToArray()), LightningMoney.MilliSatoshis(100_005_000));

    private static TrampolineOnionPacketTlv Onion =>
        new(Convert.FromHexString(Bolt4TrampolineVectors.AliceTrampolineOnion));

    private static OutgoingNodeIdTlv NextNode => new(new CompactPubKey(s_nodeId));
    private static CurrentPathKeyTlv PathKey => new(new CompactPubKey(s_nodeId));
    private static EncryptedRecipientDataTlv EncryptedData => new([0xde, 0xad]);
    private static TotalAmountMsatTlv TotalAmount => new(LightningMoney.MilliSatoshis(100_005_000));
    private static RecipientFeaturesTlv RecipientFeatures => new([0x02, 0x00, 0x00]);

    private static RecipientBlindedPathsTlv RecipientPaths =>
        new(PaymentBlindedPathCodec.DecodeList(Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner)[^417..]));

    #region Without the opt-in: as before the trampoline types were known

    [Fact]
    public void Given_AFinalPayloadWithATrampolineOnion_When_TrampolineIsNotAllowed_Then_Type20FailsWithItsOffset()
    {
        // Arrange (the offsets of trampoline-payment-onion-test.json's outer payload for Carol)
        var tlvStream = new TlvStream();
        tlvStream.Add(Amt, Cltv, PaymentData, Onion);
        var offsets = new Dictionary<BigSize, int> { [OnionPayloadTlvTypes.TrampolineOnionPacket] = 52 };
        var payload = new HopPayload(tlvStream, offsets);

        // Act
        var threeArguments = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false));
        var notAllowed = HopPayloadValidator.TryValidate(payload, true, false, false, out var error);

        // Assert
        AssertInvalidOnionPayload(threeArguments, OnionPayloadTlvTypes.TrampolineOnionPacket, 52);
        Assert.False(notAllowed);
        AssertInvalidOnionPayload(error, OnionPayloadTlvTypes.TrampolineOnionPacket, 52);
    }

    [Fact]
    public void Given_OutgoingNodeIdAtAnIntermediateHop_When_TrampolineIsNotAllowed_Then_Type14Fails()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid, NextNode);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.OutgoingNodeId, 0);
    }

    [Fact]
    public void Given_RecipientBlindedPathsAtAFinalHop_When_TrampolineIsNotAllowed_Then_Type22Fails()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, PaymentData, RecipientPaths);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.RecipientBlindedPaths, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_RecipientFeaturesAtANonBlindedHop_When_Validating_Then_TheOddRecordIsIgnored(bool allow)
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, PaymentData, RecipientFeatures);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, true, false, allow, out var error);

        // Assert
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_RecipientFeaturesAtABlindedHop_When_Validating_Then_ItIsNotAnAllowedField(bool allow)
    {
        // Arrange
        var payload = new HopPayload(EncryptedData, RecipientFeatures);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, false, true, allow, out var error);

        // Assert
        Assert.False(isValid);
        AssertInvalidOnionPayload(error, OnionPayloadTlvTypes.RecipientFeatures, 0);
    }

    #endregion

    #region With the opt-in: the trampoline node's outer payload

    public static TheoryData<string> ValidTrampolineCarriers => new()
    {
        "amt+cltv+onion (no payment_data, no MPP to the trampoline node)",
        "amt+cltv+payment_data+onion (the vector's outer payload for Carol)",
        "amt+cltv+payment_data+current_path_key+onion (the vector's outer payload for blinded Eve)",
        "amt+cltv+payment_data+onion+recipient_features (odd, ignored)"
    };

    [Theory]
    [MemberData(nameof(ValidTrampolineCarriers))]
    public void Given_AFinalPayloadCarryingATrampolineOnion_When_TrampolineIsAllowed_Then_ItIsValid(string shape)
    {
        // Arrange
        BaseTlv[] tlvs = shape switch
        {
            "amt+cltv+onion (no payment_data, no MPP to the trampoline node)" => [Amt, Cltv, Onion],
            "amt+cltv+payment_data+onion (the vector's outer payload for Carol)" => [Amt, Cltv, PaymentData, Onion],
            "amt+cltv+payment_data+current_path_key+onion (the vector's outer payload for blinded Eve)" =>
                [Amt, Cltv, PaymentData, PathKey, Onion],
            _ => [Amt, Cltv, PaymentData, Onion, RecipientFeatures]
        };
        var payload = new HopPayload(tlvs);

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, true, false, true, out var error);
        var exception = Record.Exception(() => HopPayloadValidator.Validate(payload, true, false, true));

        // Assert
        Assert.True(isValid);
        Assert.Null(error);
        Assert.Null(exception);
    }

    public static TheoryData<string, ulong> InvalidTrampolineCarriers => new()
    {
        { "short_channel_id", 6UL },
        { "outgoing_node_id", 14UL },
        { "recipient_blinded_paths", 22UL },
        { "no amt_to_forward", 2UL },
        { "no outgoing_cltv_value", 4UL }
    };

    [Theory]
    [MemberData(nameof(InvalidTrampolineCarriers))]
    public void Given_AnInvalidTrampolineCarrier_When_TrampolineIsAllowed_Then_InvalidOnionPayload(string shape,
        ulong expectedType)
    {
        // Arrange
        BaseTlv[] tlvs = shape switch
        {
            "short_channel_id" => [Amt, Cltv, Scid, PaymentData, Onion],
            "outgoing_node_id" => [Amt, Cltv, PaymentData, NextNode, Onion],
            "recipient_blinded_paths" => [Amt, Cltv, PaymentData, Onion, RecipientPaths],
            "no amt_to_forward" => [Cltv, PaymentData, Onion],
            _ => [Amt, PaymentData, Onion]
        };
        var payload = new HopPayload(tlvs);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(expectedType), 0);
    }

    [Fact]
    public void Given_ATrampolineOnionAtAnIntermediateHop_When_TrampolineIsAllowed_Then_Type20Fails()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid, Onion);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.TrampolineOnionPacket, 0);
    }

    [Fact]
    public void Given_ATrampolineOnionInABlindedFinalHop_When_TrampolineIsAllowed_Then_Type20Fails()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, EncryptedData, TotalAmount, Onion);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, true, true));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.TrampolineOnionPacket, 0);
    }

    [Fact]
    public void Given_ATrampolineCarrierWithAnUpdateAddPathKey_When_TrampolineIsAllowed_Then_EncryptedDataIsMissing()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, PaymentData, Onion);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, true, true));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.EncryptedRecipientData, 0);
    }

    [Fact]
    public void Given_OutgoingNodeIdInAPaymentOnion_When_TrampolineIsAllowed_Then_Type14StillFails()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Scid, NextNode);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.OutgoingNodeId, 0);
    }

    [Fact]
    public void Given_RegularPayloads_When_TrampolineIsAllowed_Then_TheGenericRulesStillApply()
    {
        // Arrange
        var intermediate = new HopPayload(Amt, Cltv, Scid);
        var final = new HopPayload(Amt, Cltv, PaymentData);
        var finalWithoutPaymentData = new HopPayload(Amt, Cltv);
        var blindedIntermediate = new HopPayload(EncryptedData, PathKey);

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(intermediate, false, false, true, out _));
        Assert.True(HopPayloadValidator.TryValidate(final, true, false, true, out _));
        Assert.True(HopPayloadValidator.TryValidate(blindedIntermediate, false, false, true, out _));
        // NL-1233: a missing payment_data is the final hop processor's incorrect_or_unknown_payment_details
        Assert.True(HopPayloadValidator.TryValidate(finalWithoutPaymentData, true, false, true, out _));
    }

    [Fact]
    public void Given_AnUnknownEvenType_When_TrampolineIsAllowed_Then_ItStillFails()
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(Amt, Cltv, PaymentData, Onion, new BaseTlv(new BigSize(24), [0x01]));
        var payload = new HopPayload(tlvStream);

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false, true));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(24), 0);
    }

    #endregion

    [Fact]
    public void Given_NullPayload_When_ValidatingWithTheOptIn_Then_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => HopPayloadValidator.Validate(null!, true, false, true));
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