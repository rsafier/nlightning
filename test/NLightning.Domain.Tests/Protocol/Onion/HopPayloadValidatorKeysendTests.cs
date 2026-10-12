namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Keysend and custom records (lane lh1-l3): a keysend final payload needs no <c>payment_data</c>, and records of type
/// 65536 or more are accepted whatever their parity at the final hop (as LND); below that, or at a forwarding hop, an
/// unknown even type still fails.
/// </summary>
public class HopPayloadValidatorKeysendTests
{
    private static AmtToForwardTlv Amt => new(LightningMoney.MilliSatoshis(10_000));
    private static OutgoingCltvValueTlv Cltv => new(800_000);
    private static BaseTlv Keysend => new(OnionPayloadTlvTypes.KeysendPreimage, new byte[32]);

    [Fact]
    public void Given_FinalKeysendPayloadWithoutPaymentData_When_Validating_Then_Succeeds()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Keysend, new BaseTlv(new BigSize(65536), [0x01]));

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, true, false, out var error);

        // Assert
        Assert.True(isValid, error?.Message);
        Assert.NotNull(payload.KeysendPreimage);
        var record = Assert.Single(payload.CustomRecords);
        Assert.Equal(65536UL, record.Type);
    }

    [Fact]
    public void Given_FinalPayloadWithoutPaymentDataOrKeysend_When_Validating_Then_LeftToTheFinalHopProcessor()
    {
        // Arrange (NL-1233: the final hop processor answers it with incorrect_or_unknown_payment_details)
        var payload = new HopPayload(Amt, Cltv, new BaseTlv(new BigSize(65537), [0x01]));

        // Act
        var isValid = HopPayloadValidator.TryValidate(payload, true, false, out var error);

        // Assert
        Assert.True(isValid, error?.Message);
    }

    [Fact]
    public void Given_IntermediatePayloadWithEvenCustomRecord_When_Validating_Then_FailsWithItsType()
    {
        // Arrange: the custom-record exemption is limited to the final hop; a forwarding hop keeps BOLT 1's "unknown
        // even type MUST fail" (LND accepts them at any hop; lane lh1-l3 review)
        var payload = new HopPayload(Amt, Cltv, new OnionShortChannelIdTlv(new ShortChannelId(700_000, 1, 0)),
                                     new BaseTlv(new BigSize(133773310), [0x01]));

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, false, false));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData!.Value.Span, out var type,
                                                                    out _));
        Assert.Equal(new BigSize(133773310), type);
    }

    [Fact]
    public void Given_IntermediatePayloadWithOddCustomRecord_When_Validating_Then_Succeeds()
    {
        // Arrange: an unknown odd type is ignored at any hop (BOLT 1)
        var payload = new HopPayload(Amt, Cltv, new OnionShortChannelIdTlv(new ShortChannelId(700_000, 1, 0)),
                                     new BaseTlv(new BigSize(133773311), [0x01]));

        // Act & Assert
        Assert.True(HopPayloadValidator.TryValidate(payload, false, false, out _));
    }

    [Fact]
    public void Given_UnknownEvenTypeBelowCustomRange_When_Validating_Then_Fails()
    {
        // Arrange
        var payload = new HopPayload(Amt, Cltv, Keysend, new BaseTlv(new BigSize(65534), [0x01]));

        // Act
        var exception = Assert.Throws<OnionException>(() => HopPayloadValidator.Validate(payload, true, false));

        // Assert
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData!.Value.Span, out var type,
                                                                    out _));
        Assert.Equal(new BigSize(65534), type);
    }

    [Fact]
    public void Given_BlindedFinalPayloadWithKeysend_When_Validating_Then_Fails()
    {
        // Arrange: a blinded final hop allows only its five fields
        var payload = new HopPayload(Amt, Cltv, new EncryptedRecipientDataTlv([0x01]),
                                     new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(10_000)), Keysend);

        // Act & Assert
        Assert.False(HopPayloadValidator.TryValidate(payload, true, true, out _));
    }
}