using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.FinalHop;

using Application.Payments.FinalHop;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;

/// <summary>
/// ONION M5, final node of a blinded route: <c>total_amount_msat</c> instead of <c>payment_data</c>, and our
/// <c>path_id</c> instead of the <c>payment_secret</c>; every refusal is <c>incorrect_or_unknown_payment_details</c>
/// (the switch turns it into <c>invalid_onion_blinding</c> inside the route).
/// </summary>
public class BlindedFinalHopProcessorTests
{
    private const uint Height = 800;
    private const ushort MinFinalCltv = 40;
    private const ulong AmountMsat = 100_000;
    private const uint HtlcCltv = Height + MinFinalCltv + 3;

    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly Hash s_paymentHash = SHA256.HashData(s_preimage);
    private static readonly Secret s_paymentSecret = Enumerable.Repeat((byte)0x53, 32).ToArray();

    private readonly FinalHopProcessor _processor = new(NullLogger<FinalHopProcessor>.Instance);

    private static InvoiceModel Invoice() =>
        new(s_paymentHash, s_preimage, s_paymentSecret, LightningMoney.MilliSatoshis(AmountMsat), "test",
            "lnbcrt-test", DateTimeOffset.UtcNow, 3_600, MinFinalCltv);

    private static HopPayload BlindedPayload(ulong totalMsat = AmountMsat) =>
        new(new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)), new OutgoingCltvValueTlv(HtlcCltv),
            new EncryptedRecipientDataTlv(new byte[20]),
            new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(totalMsat)));

    private static BlindedRecipientData OurData(byte[]? pathId = null) =>
        new() { PathId = pathId ?? BlindedPathId.Compute(s_preimage) };

    private FinalHopResult Evaluate(HopPayload payload, BlindedRecipientData? data, bool acceptMultiPart = false) =>
        _processor.Evaluate(Invoice(), s_paymentHash, LightningMoney.MilliSatoshis(AmountMsat), HtlcCltv, payload,
                            Height, acceptMultiPart, false, data);

    [Fact]
    public void Given_OurPathId_When_Evaluated_Then_Accepted()
    {
        // Act
        var result = Evaluate(BlindedPayload(), OurData());

        // Assert
        Assert.True(result.IsAccepted);
        Assert.Equal(LightningMoney.MilliSatoshis(AmountMsat), result.TotalMsat);
    }

    [Fact]
    public void Given_BlindedPartOfALargerTotal_When_MultiPartIsOn_Then_AcceptedAsAPart()
    {
        // Act
        var result = Evaluate(BlindedPayload(totalMsat: 2 * AmountMsat), OurData(), acceptMultiPart: true);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.Equal(LightningMoney.MilliSatoshis(2 * AmountMsat), result.TotalMsat);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("none")]
    [InlineData("no data")]
    public void Given_PathIdNotOurs_When_Evaluated_Then_IncorrectOrUnknownPaymentDetails(string variant)
    {
        // Arrange
        var data = variant switch
        {
            "other" => OurData(new byte[32]),
            "none" => new BlindedRecipientData(),
            _ => null
        };

        // Act
        var result = Evaluate(BlindedPayload(), data);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    [Fact]
    public void Given_PaymentSecretOfTheInvoiceAsPathId_When_Evaluated_Then_Refused()
    {
        // Arrange: the payment_secret is in the BOLT 11 invoice, so the payer knows it: it must not authenticate a path
        var result = Evaluate(BlindedPayload(), OurData(s_paymentSecret));

        // Assert
        Assert.False(result.IsAccepted);
    }
}