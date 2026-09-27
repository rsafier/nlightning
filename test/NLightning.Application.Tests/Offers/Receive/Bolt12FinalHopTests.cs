using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Payments.FinalHop;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;

/// <summary>
/// Plan B3-T4 (B12-RCV-01, B12-INV-02): a BOLT 12 invoice is paid only at the end of one of its blinded paths, with our
/// <c>path_id</c>; an HTLC that reaches us unblinded for it is failed with <c>incorrect_or_unknown_payment_details</c>
/// (BOLT 12 "Invoices" writer: SHOULD ignore payments that do not use one of the paths).
/// </summary>
public class Bolt12FinalHopTests
{
    private const uint Height = 800;
    private const ushort MinFinalCltv = 40;
    private const ulong AmountMsat = 100_000;
    private const uint HtlcCltv = Height + MinFinalCltv + 3;

    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly Hash s_paymentHash = SHA256.HashData(s_preimage);
    private static readonly Secret s_paymentSecret = Enumerable.Repeat((byte)0x53, 32).ToArray();

    private readonly FinalHopProcessor _processor = new(NullLogger<FinalHopProcessor>.Instance);

    private static InvoiceModel Bolt12Invoice() =>
        new(s_paymentHash, s_preimage, s_paymentSecret, LightningMoney.MilliSatoshis(AmountMsat), "coffee",
            "lni1test", DateTimeOffset.UtcNow, 7_200, MinFinalCltv,
            bolt12: new Bolt12InvoiceDetails(new Hash(new byte[32]), new byte[] { 1 }, TestPaths.Point(0x02)));

    [Fact]
    public void Given_ABolt12Invoice_When_PaidAtTheEndOfItsBlindedPath_Then_Accepted()
    {
        // Arrange
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
                                     new OutgoingCltvValueTlv(HtlcCltv), new EncryptedRecipientDataTlv(new byte[20]),
                                     new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(AmountMsat)));

        // Act
        var result = _processor.Evaluate(Bolt12Invoice(), s_paymentHash, LightningMoney.MilliSatoshis(AmountMsat),
                                         HtlcCltv, payload, Height,
                                         blindedRecipientData: new BlindedRecipientData
                                         {
                                             PathId = BlindedPathId.Compute(s_preimage)
                                         });

        // Assert
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void Given_ABolt12Invoice_When_PaidWithoutABlindedPath_Then_IncorrectOrUnknownPaymentDetails()
    {
        // Arrange: even with the invoice's (internal) payment secret the payment must come through a path
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
                                     new OutgoingCltvValueTlv(HtlcCltv),
                                     new PaymentDataTlv(s_paymentSecret, LightningMoney.MilliSatoshis(AmountMsat)));

        // Act
        var result = _processor.Evaluate(Bolt12Invoice(), s_paymentHash, LightningMoney.MilliSatoshis(AmountMsat),
                                         HtlcCltv, payload, Height);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    [Fact]
    public void Given_ABolt12Invoice_When_TheBlindedPathIdIsAnotherInvoices_Then_Refused()
    {
        // Arrange
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
                                     new OutgoingCltvValueTlv(HtlcCltv), new EncryptedRecipientDataTlv(new byte[20]),
                                     new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(AmountMsat)));

        // Act
        var result = _processor.Evaluate(Bolt12Invoice(), s_paymentHash, LightningMoney.MilliSatoshis(AmountMsat),
                                         HtlcCltv, payload, Height,
                                         blindedRecipientData: new BlindedRecipientData
                                         {
                                             PathId = BlindedPathId.Compute(new byte[32])
                                         });

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }
}