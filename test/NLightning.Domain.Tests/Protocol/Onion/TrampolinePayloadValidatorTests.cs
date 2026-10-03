using System.Buffers.Binary;
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
/// <see cref="TrampolinePayloadValidator"/>: the trampoline payload rules and the cross-onion checks of BOLTs PR 836,
/// with the values of its <c>trampoline-payment-onion-test.json</c> where they apply.
/// </summary>
public class TrampolinePayloadValidatorTests
{
    private static readonly byte[] s_eveNodeId =
        Convert.FromHexString("02edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145");

    private static readonly byte[] s_pathKey =
        Convert.FromHexString("02988face71e92c345a068f740191fd8e53be14f0bb957ef730d3c5f76087b960e");

    // The inner values of the vector: 100,000,000 msat at 800,000; Carol's outer payload: 100,005,000 msat at 800,250
    private static AmtToForwardTlv InnerAmt => new(LightningMoney.MilliSatoshis(100_000_000));
    private static OutgoingCltvValueTlv InnerCltv => new(800_000);
    private static OutgoingNodeIdTlv NextTrampoline => new(new CompactPubKey(s_eveNodeId));

    private static PaymentDataTlv InvoiceSecret =>
        new(new Secret(Enumerable.Repeat((byte)0x2a, 32).ToArray()), LightningMoney.MilliSatoshis(100_000_000));

    private static EncryptedRecipientDataTlv EncryptedData => new([0x0c, 0xcf, 0x3c]);
    private static CurrentPathKeyTlv InnerPathKey => new(new CompactPubKey(s_pathKey));
    private static TotalAmountMsatTlv InnerTotal => new(LightningMoney.MilliSatoshis(100_000_000));
    private static RecipientFeaturesTlv RecipientFeatures => new([0x02, 0x00, 0x00]);

    private static RecipientBlindedPathsTlv RecipientPaths =>
        new(PaymentBlindedPathCodec.DecodeList(Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner)[^417..]));

    private static TrampolineOnionPacketTlv Onion =>
        new(Convert.FromHexString(Bolt4TrampolineVectors.AliceTrampolineOnion));

    private static HopPayload Outer => new(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_005_000)),
                                           new OutgoingCltvValueTlv(800_250),
                                           new PaymentDataTlv(new Secret(Enumerable.Repeat((byte)0x2b, 32).ToArray()),
                                                              LightningMoney.MilliSatoshis(100_005_000)),
                                           Onion);

    private static HopPayload OuterWithPathKey => new(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_005_000)),
                                                      new OutgoingCltvValueTlv(800_250),
                                                      new CurrentPathKeyTlv(new CompactPubKey(s_eveNodeId)), Onion);

    #region Final, not blinded (BOLT 11 recipient)

    [Fact]
    public void Given_TheVectorFinalPayload_When_Validating_Then_ItIsValid()
    {
        // Arrange (Eve's payload: 2, 4, 8 with the invoice secret)
        var inner = new HopPayload(InnerAmt, InnerCltv, InvoiceSecret);

        // Act
        var isValid = TrampolinePayloadValidator.TryValidate(inner, true, Outer, false, out var error);
        var exception = Record.Exception(() => TrampolinePayloadValidator.Validate(inner, true, Outer, false));

        // Assert
        Assert.True(isValid);
        Assert.Null(error);
        Assert.Null(exception);
    }

    [Fact]
    public void Given_AFinalPayloadWithPaymentMetadata_When_Validating_Then_ItIsValid()
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, InnerCltv, InvoiceSecret, new PaymentMetadataTlv([0x01]));

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, true, Outer, false, out _));
    }

    public static TheoryData<string, ulong> InvalidFinalPayloads => new()
    {
        { "no payment_data", 8UL },
        { "no amt_to_forward", 2UL },
        { "no outgoing_cltv_value", 4UL },
        { "short_channel_id", 6UL },
        { "outgoing_node_id", 14UL },
        { "recipient_features", 21UL }
    };

    [Theory]
    [MemberData(nameof(InvalidFinalPayloads))]
    public void Given_AnInvalidFinalPayload_When_Validating_Then_InvalidOnionPayload(string shape, ulong expectedType)
    {
        // Arrange
        BaseTlv[] tlvs = shape switch
        {
            "no payment_data" => [InnerAmt, InnerCltv],
            "no amt_to_forward" => [InnerCltv, InvoiceSecret],
            "no outgoing_cltv_value" => [InnerAmt, InvoiceSecret],
            "short_channel_id" => [InnerAmt, InnerCltv, new OnionShortChannelIdTlv(new ShortChannelId(1, 2, 3)),
                                   InvoiceSecret],
            "outgoing_node_id" => [InnerAmt, InnerCltv, InvoiceSecret, NextTrampoline],
            _ => [InnerAmt, InnerCltv, InvoiceSecret, RecipientFeatures]
        };

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(new HopPayload(tlvs), true, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(expectedType), 0);
    }

    public static TheoryData<string> ValidLastTrampolineNodePayloads => new()
    {
        "recipient_blinded_paths",
        "recipient_blinded_paths + recipient_features"
    };

    [Theory]
    [MemberData(nameof(ValidLastTrampolineNodePayloads))]
    public void Given_TheLastLayerNamingRecipientBlindedPaths_When_Validating_Then_ValidAsTheLastTrampolineNode(
        string shape)
    {
        // Arrange: the payer's trampoline onion ends with the last trampoline node's payload, which pays a recipient
        // without trampoline support (trampoline-to-blinded-path-payment-onion-test.json: Carol's layer is final)
        BaseTlv[] tlvs = shape == "recipient_blinded_paths"
                             ? [InnerAmt, InnerCltv, RecipientPaths]
                             : [InnerAmt, InnerCltv, RecipientFeatures, RecipientPaths];

        // Act
        var isValid = TrampolinePayloadValidator.TryValidate(new HopPayload(tlvs), true, Outer, false, out var error);

        // Assert
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void Given_TheLastLayerWithRecipientBlindedPathsAndAnOutgoingNodeId_When_Validating_Then_Type22Fails()
    {
        // Arrange: the last trampoline node's payload follows the relay rules (outgoing_node_id or the paths)
        BaseTlv[] tlvs = [InnerAmt, InnerCltv, NextTrampoline, RecipientPaths];

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(new HopPayload(tlvs), true, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(22), 0);
    }

    #endregion

    #region Intermediate, not blinded

    public static TheoryData<string> ValidIntermediatePayloads => new()
    {
        "outgoing_node_id (the vector's payload for Carol)",
        "recipient_blinded_paths",
        "recipient_blinded_paths + recipient_features",
        "outgoing_node_id + short_channel_id (ignored)"
    };

    [Theory]
    [MemberData(nameof(ValidIntermediatePayloads))]
    public void Given_AValidIntermediatePayload_When_Validating_Then_ItIsValid(string shape)
    {
        // Arrange
        BaseTlv[] tlvs = shape switch
        {
            "outgoing_node_id (the vector's payload for Carol)" => [InnerAmt, InnerCltv, NextTrampoline],
            "recipient_blinded_paths" => [InnerAmt, InnerCltv, RecipientPaths],
            "recipient_blinded_paths + recipient_features" => [InnerAmt, InnerCltv, RecipientFeatures, RecipientPaths],
            _ => [InnerAmt, InnerCltv, new OnionShortChannelIdTlv(new ShortChannelId(1, 2, 3)), NextTrampoline]
        };

        // Act
        var isValid = TrampolinePayloadValidator.TryValidate(new HopPayload(tlvs), false, Outer, false, out var error);

        // Assert
        Assert.True(isValid);
        Assert.Null(error);
    }

    public static TheoryData<string, ulong> InvalidIntermediatePayloads => new()
    {
        { "neither outgoing_node_id nor recipient_blinded_paths", 14UL },
        { "both outgoing_node_id and recipient_blinded_paths", 22UL },
        { "recipient_features without recipient_blinded_paths", 21UL },
        { "an empty recipient_blinded_paths", 22UL },
        { "no amt_to_forward", 2UL },
        { "no outgoing_cltv_value", 4UL }
    };

    [Theory]
    [MemberData(nameof(InvalidIntermediatePayloads))]
    public void Given_AnInvalidIntermediatePayload_When_Validating_Then_InvalidOnionPayload(string shape,
        ulong expectedType)
    {
        // Arrange
        BaseTlv[] tlvs = shape switch
        {
            "neither outgoing_node_id nor recipient_blinded_paths" => [InnerAmt, InnerCltv],
            "both outgoing_node_id and recipient_blinded_paths" => [InnerAmt, InnerCltv, NextTrampoline, RecipientPaths],
            "recipient_features without recipient_blinded_paths" =>
                [InnerAmt, InnerCltv, NextTrampoline, RecipientFeatures],
            "an empty recipient_blinded_paths" => [InnerAmt, InnerCltv, new RecipientBlindedPathsTlv([])],
            "no amt_to_forward" => [InnerCltv, NextTrampoline],
            _ => [InnerAmt, NextTrampoline]
        };

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(new HopPayload(tlvs), false, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(expectedType), 0);
    }

    [Fact]
    public void Given_ACurrentPathKeyWithoutEncryptedData_When_Validating_Then_Type12Fails()
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, InnerCltv, InnerPathKey, NextTrampoline);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.CurrentPathKey, 0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Given_AnOuterPathKeyForANonBlindedPayload_When_Validating_Then_EncryptedDataIsMissing(
        bool outerHasPathKey, bool outerPayloadHasPathKey)
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, InnerCltv, NextTrampoline);
        var outer = outerPayloadHasPathKey ? OuterWithPathKey : Outer;

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, outer, outerHasPathKey));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.EncryptedRecipientData, 0);
    }

    #endregion

    #region Blinded (BOLT 12 recipient that supports trampoline)

    [Fact]
    public void Given_TheIntroductionNodePayload_When_TheOuterOnionHasNoPathKey_Then_ItIsValid()
    {
        // Arrange (Dave's payload in trampoline-to-blinded-path-payment-onion-test.json [1]: 10, 12)
        var inner = new HopPayload(EncryptedData, InnerPathKey);

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, false, Outer, false, out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Given_ALaterBlindedNodePayload_When_TheOuterOnionCarriesThePathKey_Then_ItIsValid(
        bool outerHasPathKey, bool outerPayloadHasPathKey)
    {
        // Arrange
        var inner = new HopPayload(EncryptedData);
        var outer = outerPayloadHasPathKey ? OuterWithPathKey : Outer;

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, false, outer, outerHasPathKey, out _));
    }

    [Fact]
    public void Given_TheBlindedFinalPayload_When_TheOuterOnionCarriesThePathKey_Then_ItIsValid()
    {
        // Arrange (blinded Eve's payload: 2, 4, 10, 18; Dave put the path key in the outer payload)
        var inner = new HopPayload(InnerAmt, InnerCltv, EncryptedData, InnerTotal);

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, true, OuterWithPathKey, false, out _));
    }

    [Fact]
    public void Given_APathKeyInBothOnions_When_Validating_Then_Type12Fails()
    {
        // Arrange
        var inner = new HopPayload(EncryptedData, InnerPathKey);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, OuterWithPathKey, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.CurrentPathKey, 0);
    }

    [Fact]
    public void Given_APathKeyInNeitherOnion_When_Validating_Then_Type12Fails()
    {
        // Arrange
        var inner = new HopPayload(EncryptedData);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.CurrentPathKey, 0);
    }

    public static TheoryData<string, bool, ulong> InvalidBlindedPayloads => new()
    {
        { "intermediate with amt_to_forward", false, 2UL },
        { "intermediate with outgoing_node_id", false, 14UL },
        { "intermediate with recipient_features", false, 21UL },
        { "final without total_amount_msat", true, 18UL },
        { "final without amt_to_forward", true, 2UL },
        { "final without outgoing_cltv_value", true, 4UL },
        { "final with payment_data", true, 8UL }
    };

    [Theory]
    [MemberData(nameof(InvalidBlindedPayloads))]
    public void Given_AnInvalidBlindedPayload_When_Validating_Then_InvalidOnionPayload(string shape, bool isFinal,
                                                                                         ulong expectedType)
    {
        // Arrange
        BaseTlv[] tlvs = shape switch
        {
            "intermediate with amt_to_forward" => [InnerAmt, EncryptedData],
            "intermediate with outgoing_node_id" => [EncryptedData, NextTrampoline],
            "intermediate with recipient_features" => [EncryptedData, RecipientFeatures],
            "final without total_amount_msat" => [InnerAmt, InnerCltv, EncryptedData],
            "final without amt_to_forward" => [InnerCltv, EncryptedData, InnerTotal],
            "final without outgoing_cltv_value" => [InnerAmt, EncryptedData, InnerTotal],
            _ => [InnerAmt, InnerCltv, InvoiceSecret, EncryptedData, InnerTotal]
        };

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(new HopPayload(tlvs), isFinal, OuterWithPathKey, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(expectedType), 0);
    }

    #endregion

    #region Types

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_AnUnknownEvenType_When_Validating_Then_ItFailsWithItsOffset(bool isFinal)
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(InnerAmt, InnerCltv, InvoiceSecret, NextTrampoline, new BaseTlv(new BigSize(24), [0x01]));
        var payload = new HopPayload(tlvStream, new Dictionary<BigSize, int> { [new BigSize(24)] = 81 });

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(payload, isFinal, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, new BigSize(24), 81);
    }

    [Fact]
    public void Given_AnUnknownOddType_When_Validating_Then_ItIsIgnored()
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, InnerCltv, NextTrampoline, new BaseTlv(new BigSize(25), [0x01]));

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, false, Outer, false, out _));
    }

    [Fact]
    public void Given_AnEvenCustomRecord_When_Validating_Then_OnlyTheFinalHopAcceptsIt()
    {
        // Arrange
        var custom = new BaseTlv(OnionPayloadTlvTypes.CustomRecordTypeStart, [0x01]);
        var final = new HopPayload(InnerAmt, InnerCltv, InvoiceSecret, custom);
        var intermediate = new HopPayload(InnerAmt, InnerCltv, NextTrampoline, custom);

        // Act
        var finalIsValid = TrampolinePayloadValidator.TryValidate(final, true, Outer, false, out _);
        var intermediateIsValid = TrampolinePayloadValidator.TryValidate(intermediate, false, Outer, false,
                                                                         out var error);

        // Assert
        Assert.True(finalIsValid);
        Assert.False(intermediateIsValid);
        AssertInvalidOnionPayload(error, OnionPayloadTlvTypes.CustomRecordTypeStart, 0);
    }

    [Fact]
    public void Given_ANestedTrampolineOnion_When_Validating_Then_Type20Fails()
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, InnerCltv, NextTrampoline, Onion);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.TrampolineOnionPacket, 0);
    }

    #endregion

    #region Cross-onion checks

    [Fact]
    public void Given_AnOuterExpiryBelowTheInnerOne_When_Validating_Then_FinalIncorrectCltvExpiryWithTheOuterValue()
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, new OutgoingCltvValueTlv(800_251), NextTrampoline);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, Outer, false));

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectCltvExpiry, exception.FailureCode);
        Assert.Equal(800_250U, BinaryPrimitives.ReadUInt32BigEndian(exception.FailureData!.Value.Span));
    }

    [Fact]
    public void Given_EqualExpiriesAndAmounts_When_Validating_Then_ItIsValid()
    {
        // Arrange (an outer onion with no fee or delta left for the trampoline node is allowed by the reader rule)
        var inner = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_005_000)),
                                   new OutgoingCltvValueTlv(800_250), NextTrampoline);

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, false, Outer, false, out _));
    }

    [Fact]
    public void Given_AnOuterTotalBelowTheInnerAmount_When_Validating_Then_FinalIncorrectHtlcAmountWithTheOuterTotal()
    {
        // Arrange
        var inner = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_005_001)), InnerCltv,
                                   InvoiceSecret);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, true, Outer, false));

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, exception.FailureCode);
        Assert.Equal(100_005_000UL, BinaryPrimitives.ReadUInt64BigEndian(exception.FailureData!.Value.Span));
    }

    [Fact]
    public void Given_AnMppOuterOnion_When_Validating_Then_TheOuterTotalNotThisPartIsCompared()
    {
        // Arrange: this part carries 40,000,000 msat of a 100,005,000 msat set to the trampoline node
        var outer = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(40_000_000)),
                                   new OutgoingCltvValueTlv(800_250),
                                   new PaymentDataTlv(new Secret(new byte[32]), LightningMoney.MilliSatoshis(100_005_000)),
                                   Onion);
        var inner = new HopPayload(InnerAmt, InnerCltv, NextTrampoline);

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, false, outer, false, out _));
        Assert.Equal(100_005_000UL, TrampolinePayloadValidator.GetOuterTotal(outer)!.MilliSatoshi);
    }

    [Fact]
    public void Given_AnOuterOnionWithoutPaymentData_When_Validating_Then_ItsAmountToForwardIsTheTotal()
    {
        // Arrange (no MPP to the trampoline node)
        var outer = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(99_999_999)),
                                   new OutgoingCltvValueTlv(800_250), Onion);
        var inner = new HopPayload(InnerAmt, InnerCltv, NextTrampoline);

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, outer, false));

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, exception.FailureCode);
        Assert.Equal(99_999_999UL, TrampolinePayloadValidator.GetOuterTotal(outer)!.MilliSatoshi);
    }

    [Fact]
    public void Given_AnOuterTotalAmountMsat_When_GettingTheOuterTotal_Then_ItIsUsedWithoutPaymentData()
    {
        // Arrange
        var outer = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
                                   new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(5_000)));

        // Act & Assert
        Assert.Equal(5_000UL, TrampolinePayloadValidator.GetOuterTotal(outer)!.MilliSatoshi);
        Assert.Null(TrampolinePayloadValidator.GetOuterTotal(new HopPayload()));
    }

    [Fact]
    public void Given_ABlindedIntermediatePayload_When_Validating_Then_NoCrossCheckIsMade()
    {
        // Arrange: its amount and expiry come from payment_relay once decrypted (the caller's checks)
        var outer = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1)),
                                   new OutgoingCltvValueTlv(1), Onion);
        var inner = new HopPayload(EncryptedData, InnerPathKey);

        // Act & Assert
        Assert.True(TrampolinePayloadValidator.TryValidate(inner, false, outer, false, out _));
    }

    [Fact]
    public void Given_APayloadRuleAndACrossCheckBroken_When_Validating_Then_ThePayloadRuleIsReported()
    {
        // Arrange
        var inner = new HopPayload(InnerAmt, new OutgoingCltvValueTlv(900_000));

        // Act
        var exception = Assert.Throws<OnionException>(() =>
            TrampolinePayloadValidator.Validate(inner, false, Outer, false));

        // Assert
        AssertInvalidOnionPayload(exception, OnionPayloadTlvTypes.OutgoingNodeId, 0);
    }

    #endregion

    [Fact]
    public void Given_NullPayloads_When_Validating_Then_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => TrampolinePayloadValidator.Validate(null!, true, Outer, false));
        Assert.Throws<ArgumentNullException>(() =>
            TrampolinePayloadValidator.Validate(new HopPayload(InnerAmt), true, null!, false));
        Assert.Throws<ArgumentNullException>(() => TrampolinePayloadValidator.GetOuterTotal(null!));
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