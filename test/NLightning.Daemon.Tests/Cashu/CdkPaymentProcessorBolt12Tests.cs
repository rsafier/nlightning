using Grpc.Core;

namespace NLightning.Daemon.Tests.Cashu;

using Domain.Cashu.Enums;
using Domain.Cashu.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Models;
using NLightning.Cashu.PaymentProcessor.Grpc;

/// <summary>
/// BOLT 12 in the CDK payment processor (NUT-25, NL-997): mint quotes as our offers named by offer id, each paid
/// invoice a payment of its own, and melts of offers named by the mint's quote id, stored before they are sent.
/// </summary>
public sealed class CdkPaymentProcessorBolt12Tests : CdkProcessorTestBase
{
    [Fact]
    public async Task Given_ABolt12MintQuote_When_CreatePayment_Then_AnOfferLabelledForTheMintNamedByItsId()
    {
        // Arrange
        var offer = Offer(Hash(0x0A), LightningMoney.Satoshis(500));
        CreateOfferRequest? asked = null;
        OfferService.Setup(s => s.CreateOfferAsync(It.IsAny<CreateOfferRequest>(), It.IsAny<CancellationToken>()))
                    .Callback<CreateOfferRequest, CancellationToken>((r, _) => asked = r)
                    .ReturnsAsync(new CreatedOffer(offer, null));
        var expiry = (ulong)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        // Act
        var response = await Client.CreatePaymentAsync(new CreatePaymentRequest
        {
            Options = new IncomingPaymentOptions
            {
                Bolt12 = new Bolt12IncomingPaymentOptions
                {
                    Amount = new AmountMessage { Value = 500, Unit = "sat" },
                    UnixExpiry = expiry
                }
            }
        }, cancellationToken: Ct);

        // Assert: an amount needs a description, so the processor gives one
        Assert.Equal(PaymentIdentifierType.OfferId, response.RequestIdentifier.Type);
        Assert.Equal(offer.OfferId.ToString(), response.RequestIdentifier.Id);
        Assert.Equal(offer.Bolt12, response.Request);
        Assert.Equal(expiry, response.Expiry);
        Assert.Equal(LightningMoney.Satoshis(500), asked!.Amount);
        Assert.Equal("Cashu mint quote", asked.Description);
        Assert.Equal(MintLabel, asked.Labels.Label);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds((long)expiry), asked.AbsoluteExpiry);
    }

    [Fact]
    public async Task Given_TwoPaidInvoicesOfAnOffer_When_StreamedAndChecked_Then_EachIsAPaymentOfTheOffer()
    {
        // Arrange
        var offerId = Hash(0x0B);
        var first = Bolt12Invoice(Hash(0x21), offerId, LightningMoney.MilliSatoshis(3_000_500));
        var second = Bolt12Invoice(Hash(0x22), offerId, LightningMoney.Satoshis(1_000));
        InvoiceService.Setup(s => s.GetInvoiceAsync(first.PaymentHash, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(first);
        InvoiceRepository.Setup(r => r.ListSettledByOfferIdAsync(offerId)).ReturnsAsync([first, second]);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();

        // Act
        Hub.Publish(new InvoiceSettledEvent(first.PaymentHash, first.AmountReceived!, DateTimeOffset.UtcNow));
        var check = await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.OfferId, Id = offerId.ToString() }
        }, cancellationToken: Ct);

        // Assert: by offer id, payment id the invoice's hash, amounts floored to sats
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        var received = stream.ResponseStream.Current.PaymentReceived;
        Assert.Equal(PaymentIdentifierType.OfferId, received.PaymentIdentifier.Type);
        Assert.Equal(offerId.ToString(), received.PaymentIdentifier.Id);
        Assert.Equal(first.PaymentHash.ToString(), received.PaymentId);
        Assert.Equal(3_000UL, received.PaymentAmount.Value);
        Assert.Equal([first.PaymentHash.ToString(), second.PaymentHash.ToString()],
                     check.Payments.Select(p => p.PaymentId));
        Assert.Equal([3_000UL, 1_000UL], check.Payments.Select(p => p.PaymentAmount.Value));
    }

    [Fact]
    public async Task Given_AnOfferToMelt_When_GetPaymentQuote_Then_ItsAmountTheReserveAndTheQuoteIdStored()
    {
        // Arrange: 10,000,000 msat; 0.5 % is 50 sat
        var offer = RegtestOffer(10_000_000);

        // Act
        var quote = await Client.GetPaymentQuoteAsync(new PaymentQuoteRequest
        {
            Request = offer,
            Unit = "sat",
            RequestType = OutgoingPaymentRequestType.Bolt12Offer,
            QuoteId = "melt-b12"
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(PaymentIdentifierType.QuoteId, quote.RequestIdentifier.Type);
        Assert.Equal("melt-b12", quote.RequestIdentifier.Id);
        Assert.Equal(10_000UL, quote.Amount.Value);
        Assert.Equal(50UL, quote.Fee.Value);
        var stored = await Quotes.GetAsync("melt-b12");
        Assert.Equal(CashuQuoteMethod.Bolt12, stored!.Method);
        Assert.Equal(CashuQuoteState.Created, stored.State);
        Assert.Equal(offer, stored.Request);
    }

    [Fact]
    public async Task Given_AnOfferWithoutAmount_When_GetPaymentQuote_Then_TheMintsAmountOrInvalidArgument()
    {
        // Arrange
        var offer = RegtestOffer(null);

        // Act
        var quote = await Client.GetPaymentQuoteAsync(new PaymentQuoteRequest
        {
            Request = offer,
            Unit = "sat",
            RequestType = OutgoingPaymentRequestType.Bolt12Offer,
            QuoteId = "melt-any",
            Options = new MeltOptions { Amountless = new Amountless { AmountMsat = 2_000_000 } }
        }, cancellationToken: Ct);
        var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await Client.GetPaymentQuoteAsync(
            new PaymentQuoteRequest
            {
                Request = offer,
                Unit = "sat",
                RequestType = OutgoingPaymentRequestType.Bolt12Offer,
                QuoteId = "melt-none"
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(2_000UL, quote.Amount.Value);
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Given_ABolt12Melt_When_ThePaymentSucceeds_Then_PaidByQuoteIdAndAReplayDoesNotPayAgain()
    {
        // Arrange
        var offer = RegtestOffer(10_000_000);
        var hash = Hash(0x31);
        var payment = new PaymentModel(hash, null, Payee, LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(3),
                                       DateTimeOffset.UtcNow)
        { Label = MintLabel };
        payment.Succeed(Preimage, DateTimeOffset.UtcNow);
        PayOfferRequest? asked = null;
        PayOfferOptions? options = null;
        OfferPaymentService.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                       It.IsAny<CancellationToken>()))
                           .Callback<PayOfferRequest, PayOfferOptions, CancellationToken>((r, o, _) =>
                            {
                                asked = r;
                                options = o;
                            })
                           .ReturnsAsync(new PayOfferResult(Fetched(hash), new PayInvoiceResult(payment, 1, 1)));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        var request = new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant
            {
                Bolt12 = new Bolt12OutgoingPaymentOptions
                {
                    Offer = offer,
                    QuoteId = "melt-b12-pay",
                    MaxFeeAmount = new AmountMessage { Value = 50, Unit = "sat" }
                }
            },
            Unit = "sat"
        };

        // Act
        var response = await Client.MakePaymentAsync(request, cancellationToken: Ct);
        var replay = await Client.MakePaymentAsync(request, cancellationToken: Ct);
        var check = await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.QuoteId, Id = "melt-b12-pay" }
        }, cancellationToken: Ct);

        // Assert: the offer's amount is not repeated in the request, the reserve is the fee limit
        Assert.Equal(QuoteState.Paid, response.Status);
        Assert.Equal(PaymentIdentifierType.QuoteId, response.PaymentIdentifier.Type);
        Assert.Equal("melt-b12-pay", response.PaymentIdentifier.Id);
        Assert.Equal(10_003UL, response.TotalSpent.Value);
        Assert.Null(asked!.Amount);
        Assert.Equal(LightningMoney.Satoshis(50), options!.Payment.MaxFee);
        Assert.Contains("cdk_quote=melt-b12-pay", options.Payment.Labels.TagStrings);
        Assert.Equal(QuoteState.Paid, replay.Status);
        Assert.Equal(QuoteState.Paid, check.Status);
        OfferPaymentService.Verify(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                        It.IsAny<CancellationToken>()), Times.Once);
        var stored = await Quotes.GetAsync("melt-b12-pay");
        Assert.Equal(CashuQuoteState.Paid, stored!.State);
        Assert.Equal(hash, stored.PaymentHash);
    }

    [Fact]
    public async Task Given_ABolt12MeltWhoseInvoiceNeverCame_When_MakePayment_Then_FailedAndTheMintMayRetry()
    {
        // Arrange
        var offer = RegtestOffer(10_000_000);
        OfferPaymentService.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                       It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new PayOfferResult(new FetchInvoiceResult(FetchInvoiceStatus.TimedOut, null,
                                                                                   3, "no answer"), null));

        // Act
        var response = await Client.MakePaymentAsync(new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant
            {
                Bolt12 = new Bolt12OutgoingPaymentOptions { Offer = offer, QuoteId = "melt-b12-fail" }
            }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(QuoteState.Failed, response.Status);
        var stored = await Quotes.GetAsync("melt-b12-fail");
        Assert.Equal(CashuQuoteState.Failed, stored!.State);
        Assert.Contains("TimedOut", stored.FailureReason);
    }

    [Fact]
    public async Task Given_ABolt12MeltInterruptedByARestart_When_CheckedAndReplayed_Then_PendingAndNotSentAgain()
    {
        // Arrange: saved Dispatching before a crash; no payment hash is known
        var quote = new CashuQuoteModel("melt-b12-crash", CashuQuoteMethod.Bolt12, CashuQuoteDirection.Outgoing,
                                        LightningMoney.Satoshis(10_000), DateTimeOffset.UtcNow)
        {
            Request = RegtestOffer(10_000_000)
        };
        quote.SetState(CashuQuoteState.Dispatching, DateTimeOffset.UtcNow);
        Quotes.Add(quote);

        // Act
        var check = await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.QuoteId, Id = "melt-b12-crash" }
        }, cancellationToken: Ct);
        var replay = await Client.MakePaymentAsync(new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant
            {
                Bolt12 = new Bolt12OutgoingPaymentOptions { Offer = quote.Request, QuoteId = "melt-b12-crash" }
            }
        }, cancellationToken: Ct);

        // Assert: never answered unpaid, never paid twice
        Assert.Equal(QuoteState.Pending, check.Status);
        Assert.Equal(QuoteState.Pending, replay.Status);
        OfferPaymentService.Verify(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                        It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_ABolt12MeltPendingAtTheTimeout_When_ItsPaymentFails_Then_TheStreamFailsTheQuote()
    {
        // Arrange: a melt answered pending
        var offer = RegtestOffer(10_000_000);
        var hash = Hash(0x32);
        var payment = new PaymentModel(hash, null, Payee, LightningMoney.Satoshis(10_000), LightningMoney.Zero,
                                       DateTimeOffset.UtcNow)
        { Label = MintLabel };
        OfferPaymentService.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                       It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new PayOfferResult(Fetched(hash), new PayInvoiceResult(payment, 1, 1)));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();
        var pending = await Client.MakePaymentAsync(new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant
            {
                Bolt12 = new Bolt12OutgoingPaymentOptions { Offer = offer, QuoteId = "melt-b12-later" }
            }
        }, cancellationToken: Ct);

        // Act
        payment.Fail(null, null, "no route", DateTimeOffset.UtcNow);
        Hub.Publish(new PaymentFailedEvent(hash, "no route", DateTimeOffset.UtcNow));

        // Assert
        Assert.Equal(QuoteState.Pending, pending.Status);
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        Assert.Equal("melt-b12-later", stream.ResponseStream.Current.PaymentFailed.QuoteId);
        Assert.Equal(CashuQuoteState.Failed, (await Quotes.GetAsync("melt-b12-later"))!.State);
    }

    [Fact]
    public async Task Given_ABolt12MeltBetweenTwoAttempts_When_MakePaymentAnswers_Then_PendingNotFailed()
    {
        // Arrange (NL-999): the payment row reads Failed while the payment service still retries it
        var hash = Hash(0x41);
        var payment = new PaymentModel(hash, null, Payee, LightningMoney.Satoshis(10_000), LightningMoney.Zero,
                                       DateTimeOffset.UtcNow)
        { Label = MintLabel };
        payment.Fail(null, null, "Retrying.", DateTimeOffset.UtcNow);
        OfferPaymentService.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                       It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new PayOfferResult(Fetched(hash), new PayInvoiceResult(payment, 1, 1)));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        PaymentService.Setup(s => s.IsPaying(hash)).Returns(true);

        // Act
        var response = await Client.MakePaymentAsync(Bolt12Melt("melt-b12-retry"), cancellationToken: Ct);

        // Assert
        Assert.Equal(QuoteState.Pending, response.Status);
        Assert.Equal(CashuQuoteState.Pending, (await Quotes.GetAsync("melt-b12-retry"))!.State);
    }

    [Fact]
    public async Task Given_TheOfferPayerRefusing_When_MakePayment_Then_FailedPreconditionAndTheQuoteReadsPending()
    {
        // Arrange: the payer refuses (for instance already paying the fetched invoice); nothing says it was not sent
        OfferPaymentService.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                       It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new InvalidOperationException("Already paying."));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(
                        async () => await Client.MakePaymentAsync(Bolt12Melt("melt-b12-refused"), cancellationToken: Ct));
        var check = await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.QuoteId, Id = "melt-b12-refused" }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal(QuoteState.Pending, check.Status);
    }

    [Fact]
    public async Task Given_AnOfferWithInvoicesNotOfTheMint_When_CheckIncomingPayment_Then_OnlyTheMintsAreListed()
    {
        // Arrange (NL-999)
        var offerId = Hash(0x0C);
        var mints = Bolt12Invoice(Hash(0x23), offerId, LightningMoney.Satoshis(1_000));
        var other = Bolt12Invoice(Hash(0x24), offerId, LightningMoney.Satoshis(2_000));
        other.Label = "coffee";
        InvoiceRepository.Setup(r => r.ListSettledByOfferIdAsync(offerId)).ReturnsAsync([mints, other]);

        // Act
        var check = await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.OfferId, Id = offerId.ToString() }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal([mints.PaymentHash.ToString()], check.Payments.Select(p => p.PaymentId));
    }

    private static MakePaymentRequest Bolt12Melt(string quoteId) => new()
    {
        PaymentOptions = new OutgoingPaymentVariant
        {
            Bolt12 = new Bolt12OutgoingPaymentOptions { Offer = RegtestOffer(10_000_000), QuoteId = quoteId }
        },
        Unit = "sat"
    };

    private static OfferModel Offer(Hash offerId, LightningMoney amount) =>
        new(offerId, "lno1test", new byte[] { 1 }, "coffee", amount, null, null, null, null, new byte[] { 2 },
            OfferIssuerKind.NodeId, false, DateTimeOffset.UtcNow);

    private static InvoiceModel Bolt12Invoice(Hash paymentHash, Hash offerId, LightningMoney received)
    {
        var invoice = new InvoiceModel(paymentHash, Preimage, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()),
                                       received, null, null, DateTimeOffset.UtcNow, 7_200, 18, InvoiceStatus.Settled,
                                       received, DateTimeOffset.UtcNow,
                                       new Bolt12InvoiceDetails(offerId, new byte[] { 3 }, Payee))
        {
            Label = MintLabel
        };
        return invoice;
    }

    private static FetchInvoiceResult Fetched(Hash paymentHash) =>
        new(FetchInvoiceStatus.Received,
            new FetchedBolt12Invoice(new byte[] { 1 }, new byte[] { 2 }, Payee, LightningMoney.Satoshis(10_000),
                                     paymentHash, DateTimeOffset.UtcNow, 7_200, 1), 1);
}