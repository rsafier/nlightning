using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.FinalHop;

using Application.Payments.FinalHop;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

/// <summary>
/// ONION M4-T3: BOLT 4 final-node rules, no MPP. Every invoice-related failure is
/// <c>incorrect_or_unknown_payment_details</c> (0x400F) with (HTLC amount, current height); HTLC-vs-onion mismatches
/// are <c>final_incorrect_cltv_expiry</c> (0x0012) and <c>final_incorrect_htlc_amount</c> (0x0013).
/// </summary>
public class FinalHopProcessorTests
{
    private const uint Height = 800;
    private const ushort MinFinalCltv = 40;
    private const ulong AmountMsat = 100_000;
    private const uint HtlcCltv = Height + MinFinalCltv + 3;

    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly Hash s_paymentHash = SHA256.HashData(s_preimage);
    private static readonly Secret s_paymentSecret = Enumerable.Repeat((byte)0x53, 32).ToArray();

    private readonly FinalHopProcessor _processor = new(NullLogger<FinalHopProcessor>.Instance);

    private static InvoiceModel CreateInvoice(ulong? amountMsat = AmountMsat, DateTimeOffset? createdAt = null,
                                              InvoiceStatus status = InvoiceStatus.Open)
    {
        var invoice = new InvoiceModel(s_paymentHash, s_preimage, s_paymentSecret,
                                       amountMsat is null ? null : LightningMoney.MilliSatoshis(amountMsat.Value),
                                       "test", "lnbcrt-test", createdAt ?? DateTimeOffset.UtcNow, 3_600,
                                       MinFinalCltv);
        switch (status)
        {
            case InvoiceStatus.Canceled:
                invoice.Cancel();
                break;
            case InvoiceStatus.Accepted:
                invoice.Accept(LightningMoney.MilliSatoshis(AmountMsat));
                break;
            case InvoiceStatus.Settled:
                invoice.Accept(LightningMoney.MilliSatoshis(AmountMsat));
                invoice.Settle(DateTimeOffset.UtcNow);
                break;
        }

        return invoice;
    }

    private static HopPayload CreatePayload(ulong amtToForwardMsat = AmountMsat, uint outgoingCltv = HtlcCltv,
                                            ulong? totalMsat = null, Secret? paymentSecret = null,
                                            bool withPaymentData = true)
    {
        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(LightningMoney.MilliSatoshis(amtToForwardMsat)),
            new OutgoingCltvValueTlv(outgoingCltv)
        };
        if (withPaymentData)
            tlvs.Add(new PaymentDataTlv(paymentSecret ?? s_paymentSecret,
                                        LightningMoney.MilliSatoshis(totalMsat ?? amtToForwardMsat)));

        return new HopPayload(tlvs.ToArray());
    }

    private FinalHopResult Evaluate(InvoiceModel? invoice, HopPayload payload, ulong htlcAmountMsat = AmountMsat,
                                    uint htlcCltv = HtlcCltv) =>
        _processor.Evaluate(invoice, s_paymentHash, LightningMoney.MilliSatoshis(htlcAmountMsat), htlcCltv, payload,
                            Height);

    private static void AssertUnknownPaymentDetails(FinalHopResult result, ulong htlcAmountMsat = AmountMsat)
    {
        Assert.False(result.IsAccepted);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
        Assert.Equal(htlcAmountMsat, result.Failure.HtlcAmount!.MilliSatoshi);
        Assert.Equal(Height, result.Failure.Height);
        Assert.Null(result.Invoice);
        Assert.Null(result.Preimage);
    }

    [Fact]
    public void Given_MatchingHtlc_When_Evaluated_Then_AcceptedWithPreimageAndHtlcAmount()
    {
        // Arrange
        var invoice = CreateInvoice();

        // Act
        var result = Evaluate(invoice, CreatePayload(), htlcAmountMsat: AmountMsat + 7);

        // Assert
        Assert.True(result.IsAccepted, result.Reason);
        Assert.Same(invoice, result.Invoice);
        Assert.Equal(new Secret(s_preimage), result.Preimage);
        Assert.Equal(AmountMsat + 7, result.AmountReceived!.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Open, invoice.Status); // the processor never mutates the invoice
    }

    [Fact]
    public void Given_HtlcAmountBelowAmtToForward_When_Evaluated_Then_FinalIncorrectHtlcAmount0x0013()
    {
        // Act
        var result = Evaluate(CreateInvoice(), CreatePayload(), htlcAmountMsat: AmountMsat - 1);

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, result.Failure!.Code);
        Assert.Equal(0x0013, (ushort)result.Failure.Code);
        Assert.Equal(AmountMsat - 1, result.Failure.HtlcAmount!.MilliSatoshi);
    }

    [Fact]
    public void Given_HtlcCltvBelowOutgoingCltvValue_When_Evaluated_Then_FinalIncorrectCltvExpiry0x0012()
    {
        // Act
        var result = Evaluate(CreateInvoice(), CreatePayload(outgoingCltv: HtlcCltv + 1), htlcCltv: HtlcCltv);

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectCltvExpiry, result.Failure!.Code);
        Assert.Equal(0x0012, (ushort)result.Failure.Code);
        Assert.Equal(HtlcCltv, result.Failure.CltvExpiry);
    }

    [Fact]
    public void Given_UnknownHashAndTamperedAmount_When_Evaluated_Then_0x0013TakesPrecedence()
    {
        // Act: the penultimate hop's tampering is reported before the invoice lookup
        var result = Evaluate(null, CreatePayload(), htlcAmountMsat: AmountMsat - 1);

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, result.Failure!.Code);
    }

    [Fact]
    public async Task Given_TamperedAmount_When_Processed_Then_InvoiceIsNotLookedUp()
    {
        // Arrange
        var invoices = new Mock<IInvoiceDbRepository>();

        // Act
        var result = await _processor.ProcessAsync(invoices.Object, s_paymentHash,
                                                   LightningMoney.MilliSatoshis(AmountMsat - 1), HtlcCltv,
                                                   CreatePayload(), Height);

        // Assert
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, result.Failure!.Code);
        invoices.Verify(x => x.GetByPaymentHashAsync(It.IsAny<Hash>()), Times.Never);
    }

    [Fact]
    public async Task Given_StoredInvoice_When_Processed_Then_LooksItUpByHashAndAccepts()
    {
        // Arrange
        var invoices = new Mock<IInvoiceDbRepository>();
        invoices.Setup(x => x.GetByPaymentHashAsync(s_paymentHash)).ReturnsAsync(CreateInvoice());

        // Act
        var result = await _processor.ProcessAsync(invoices.Object, s_paymentHash,
                                                   LightningMoney.MilliSatoshis(AmountMsat), HtlcCltv,
                                                   CreatePayload(), Height);

        // Assert
        Assert.True(result.IsAccepted, result.Reason);
        invoices.Verify(x => x.GetByPaymentHashAsync(s_paymentHash), Times.Once);
    }

    [Fact]
    public void Given_UnknownPaymentHash_When_Evaluated_Then_0x400FWithHtlcAmountAndHeight()
    {
        // Act
        var result = Evaluate(null, CreatePayload());

        // Assert
        AssertUnknownPaymentDetails(result);
        Assert.Equal(0x400F, (ushort)result.Failure!.Code);
    }

    [Fact]
    public void Given_WrongPaymentSecret_When_Evaluated_Then_0x400F()
    {
        // Act
        var result = Evaluate(CreateInvoice(),
                              CreatePayload(paymentSecret: Enumerable.Repeat((byte)0x54, 32).ToArray()));

        // Assert
        AssertUnknownPaymentDetails(result);
    }

    [Fact]
    public void Given_NoPaymentData_When_Evaluated_Then_0x400F()
    {
        // Act
        var result = Evaluate(CreateInvoice(), CreatePayload(withPaymentData: false));

        // Assert
        AssertUnknownPaymentDetails(result);
    }

    [Theory]
    [InlineData(InvoiceStatus.Canceled)]
    [InlineData(InvoiceStatus.Accepted)]
    [InlineData(InvoiceStatus.Settled)]
    public void Given_InvoiceNotOpen_When_Evaluated_Then_0x400F(InvoiceStatus status)
    {
        // Act
        var result = Evaluate(CreateInvoice(status: status), CreatePayload());

        // Assert
        AssertUnknownPaymentDetails(result);
    }

    [Fact]
    public void Given_ExpiredInvoice_When_Evaluated_Then_0x400F()
    {
        // Act
        var result = Evaluate(CreateInvoice(createdAt: DateTimeOffset.UtcNow.AddHours(-2)), CreatePayload());

        // Assert
        AssertUnknownPaymentDetails(result);
    }

    [Fact]
    public void Given_TotalMsatAboveAmtToForward_When_Evaluated_Then_0x400FBecauseMppIsNotSupported()
    {
        // Act: a multi-part payment of 2 x 100000 msat
        var result = Evaluate(CreateInvoice(2 * AmountMsat), CreatePayload(totalMsat: 2 * AmountMsat));

        // Assert
        AssertUnknownPaymentDetails(result);
    }

    [Theory]
    [InlineData(AmountMsat - 1, false)]
    [InlineData(AmountMsat, true)]
    [InlineData(2 * AmountMsat, true)]
    [InlineData(2 * AmountMsat + 1, false)]
    public void Given_AmountPaid_When_Evaluated_Then_AcceptedOnlyBetweenAmountAndTwiceIt(ulong paidMsat,
        bool accepted)
    {
        // Act
        var result = Evaluate(CreateInvoice(), CreatePayload(paidMsat), htlcAmountMsat: paidMsat);

        // Assert
        if (accepted)
            Assert.True(result.IsAccepted, result.Reason);
        else
            AssertUnknownPaymentDetails(result, paidMsat);
    }

    [Fact]
    public void Given_AnyAmountInvoice_When_AnyAmountPaid_Then_Accepted()
    {
        // Act
        var result = Evaluate(CreateInvoice(amountMsat: null), CreatePayload(1), htlcAmountMsat: 1);

        // Assert
        Assert.True(result.IsAccepted, result.Reason);
    }

    [Theory]
    [InlineData(Height + MinFinalCltv - 1, false)]
    [InlineData(Height + MinFinalCltv, true)]
    public void Given_HtlcCltvRelativeToMinFinalCltvExpiry_When_Evaluated_Then_TooSoonIs0x400F(uint htlcCltv,
        bool accepted)
    {
        // Act
        var result = Evaluate(CreateInvoice(), CreatePayload(outgoingCltv: htlcCltv), htlcCltv: htlcCltv);

        // Assert
        if (accepted)
            Assert.True(result.IsAccepted, result.Reason);
        else
            AssertUnknownPaymentDetails(result);
    }
}