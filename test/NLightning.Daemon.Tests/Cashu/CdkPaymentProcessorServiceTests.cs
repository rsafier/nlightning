using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Daemon.Tests.Cashu;

using Bolt11.Models;
using Domain.Accounting.Labels;
using Domain.Cashu.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Models;
using Domain.Protocol.ValueObjects;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Cashu.PaymentProcessor.Grpc;

/// <summary>
/// The CDK payment processor's settings, BOLT 11 and restart behavior (Cashu plan C1, NL-992; NL-997).
/// </summary>
public sealed class CdkPaymentProcessorServiceTests : CdkProcessorTestBase
{
    [Fact]
    public async Task Given_TheProcessor_When_GetSettings_Then_EveryServedMethodInSat()
    {
        // Act
        var settings = await Client.GetSettingsAsync(new EmptyRequest(), cancellationToken: Ct);

        // Assert
        Assert.Equal("sat", settings.Unit);
        Assert.True(settings.Bolt11.Amountless);
        Assert.False(settings.Bolt11.Mpp);
        Assert.True(settings.Bolt12.Amountless);
        Assert.Equal(2u, settings.Onchain.Confirmations);
        Assert.Equal(1_000UL, settings.Onchain.MinReceiveAmountSat);
        Assert.Equal(1_000UL, settings.Onchain.MinSendAmountSat);
    }

    [Fact]
    public async Task Given_ANodeWithoutOffersOrWallet_When_GetSettingsAndBolt12_Then_Bolt11OnlyAndUnimplemented()
    {
        // Arrange: on-chain enabled, but none of the services it needs
        await using var provider = BuildProvider(new CashuPaymentProcessorOptions
        {
            Enabled = true,
            OnchainEnabled = true
        }, services: false);
        var service = provider.GetRequiredService<CdkPaymentProcessorService>();

        // Act
        var settings = await service.GetSettings(new EmptyRequest(), null!);
        var error = await Assert.ThrowsAsync<RpcException>(() => service.CreatePayment(new CreatePaymentRequest
        {
            Options = new IncomingPaymentOptions { Bolt12 = new Bolt12IncomingPaymentOptions() }
        }, null!));

        // Assert
        Assert.NotNull(settings.Bolt11);
        Assert.Null(settings.Bolt12);
        Assert.Null(settings.Onchain);
        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
    }

    [Fact]
    public async Task Given_OffersOffInTheOptions_When_GetSettings_Then_NoBolt12()
    {
        // Arrange
        await using var provider = BuildProvider(new CashuPaymentProcessorOptions
        {
            Enabled = true,
            Bolt12Enabled = false
        });
        var service = provider.GetRequiredService<CdkPaymentProcessorService>();

        // Act
        var settings = await service.GetSettings(new EmptyRequest(), null!);

        // Assert: on-chain is off by default too
        Assert.Null(settings.Bolt12);
        Assert.Null(settings.Onchain);
    }

    [Fact]
    public async Task Given_ABolt11MintQuote_When_CreatePayment_Then_AnInvoiceLabelledForTheMintIsCreated()
    {
        // Arrange
        var hash = Hash(0x01);
        InvoiceService.Setup(s => s.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), "mint", null,
                                                       It.Is<SourceLabels>(l => l.Label == MintLabel),
                                                       It.IsAny<CancellationToken>()))
                      .ReturnsAsync(InvoiceRow(hash, InvoiceStatus.Open));

        // Act
        var response = await Client.CreatePaymentAsync(new CreatePaymentRequest
        {
            Options = new IncomingPaymentOptions
            {
                Bolt11 = new Bolt11IncomingPaymentOptions
                {
                    Amount = new AmountMessage { Value = 1_000, Unit = "sat" },
                    Description = "mint"
                }
            }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(PaymentIdentifierType.PaymentHash, response.RequestIdentifier.Type);
        Assert.Equal(hash.ToString(), response.RequestIdentifier.Hash);
        Assert.Equal("lnbcrt1test", response.Request);
        Assert.True(response.Expiry > 0);
    }

    [Fact]
    public async Task Given_AnInvoiceToMelt_When_GetPaymentQuote_Then_AmountAndTheFeeReserveInSat()
    {
        // Arrange: 1,000,500 msat; 0.5 % is 5,002 msat, above the 5,000 msat floor
        var (bolt11, hash) = SignedBolt11(LightningMoney.MilliSatoshis(1_000_500));

        // Act
        var quote = await Client.GetPaymentQuoteAsync(new PaymentQuoteRequest
        {
            Request = bolt11,
            Unit = "sat",
            RequestType = OutgoingPaymentRequestType.Bolt11Invoice,
            QuoteId = "q1"
        }, cancellationToken: Ct);

        // Assert: both rounded up to whole sats
        Assert.Equal(hash, quote.RequestIdentifier.Hash);
        Assert.Equal(1_001UL, quote.Amount.Value);
        Assert.Equal(6UL, quote.Fee.Value);
        Assert.Equal(QuoteState.Unpaid, quote.State);
    }

    [Fact]
    public async Task Given_AnotherUnit_When_GetPaymentQuote_Then_InvalidArgument()
    {
        // Arrange
        var (bolt11, _) = SignedBolt11(LightningMoney.Satoshis(1_000));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.GetPaymentQuoteAsync(
            new PaymentQuoteRequest
            {
                Request = bolt11,
                Unit = "usd",
                RequestType = OutgoingPaymentRequestType.Bolt11Invoice
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Given_AMelt_When_MakePaymentSucceeds_Then_PaidWithProofTheQuoteIsStoredAndTheStreamNamesIt()
    {
        // Arrange
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var payment = Payment(hash, LightningMoney.Satoshis(1_000), LightningMoney.MilliSatoshis(1_500));
        payment.Succeed(Preimage, DateTimeOffset.UtcNow);
        PayInvoiceOptions? usedOptions = null;
        PaymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .Callback<string, LightningMoney?, PayInvoiceOptions, CancellationToken>(
                           (_, _, o, _) => usedOptions = o)
                      .ReturnsAsync(new PayInvoiceResult(payment, 1, 1));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();

        // Act
        var response = await Client.MakePaymentAsync(new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant
            {
                Bolt11 = new Bolt11OutgoingPaymentOptions
                {
                    Bolt11 = bolt11,
                    QuoteId = "melt-1",
                    MaxFeeAmount = new AmountMessage { Value = 5, Unit = "sat" }
                }
            },
            Unit = "sat"
        }, cancellationToken: Ct);
        Hub.Publish(new PaymentSucceededEvent(hash, payment.Amount, payment.Fee, Preimage, DateTimeOffset.UtcNow));

        // Assert: paid, 1,001.5 sat spent rounds up to 1,002, the fee limit, label and quote tag went to the payment
        Assert.Equal(QuoteState.Paid, response.Status);
        Assert.Equal(1_002UL, response.TotalSpent.Value);
        Assert.Equal(Convert.ToHexString((byte[])Preimage).ToLowerInvariant(), response.PaymentProof);
        Assert.Equal(LightningMoney.Satoshis(5), usedOptions!.MaxFee);
        Assert.Equal(MintLabel, usedOptions.Labels.Label);
        Assert.Contains("cdk_quote=melt-1", usedOptions.Labels.TagStrings);
        var stored = await Quotes.GetAsync("melt-1");
        Assert.Equal(CashuQuoteState.Paid, stored!.State);
        Assert.Equal(hash, stored.PaymentHash);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        Assert.Equal("melt-1", stream.ResponseStream.Current.PaymentSuccessful.QuoteId);
        Assert.Equal(QuoteState.Paid, stream.ResponseStream.Current.PaymentSuccessful.Details.Status);
        Assert.Equal(hash.ToString(), stream.ResponseStream.Current.PaymentSuccessful.Details.PaymentIdentifier.Hash);
    }

    [Fact]
    public async Task Given_APaymentStillInFlightAtTheTimeout_When_ItSucceedsAfterARestart_Then_TheStreamNamesTheQuote()
    {
        // Arrange: the melt answers pending; the node restarts (a new processor over the same quotes)
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(2_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var payment = Payment(hash, LightningMoney.Satoshis(2_000), LightningMoney.Satoshis(1));
        PaymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new PayInvoiceResult(payment, 1, 1));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        var pending = await Client.MakePaymentAsync(new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant
            {
                Bolt11 = new Bolt11OutgoingPaymentOptions { Bolt11 = bolt11, QuoteId = "melt-restart" }
            }
        }, cancellationToken: Ct);
        await using var restarted = NewServiceInstance();

        // Act
        payment.Succeed(Preimage, DateTimeOffset.UtcNow);
        var mapped = await restarted.ToEventResponseAsync(
            new PaymentSucceededEvent(hash, payment.Amount, payment.Fee, Preimage, DateTimeOffset.UtcNow), Ct);

        // Assert
        Assert.Equal(QuoteState.Pending, pending.Status);
        Assert.Equal("melt-restart", mapped!.PaymentSuccessful.QuoteId);
        Assert.Equal(CashuQuoteState.Paid, (await Quotes.GetAsync("melt-restart"))!.State);
    }

    [Fact]
    public async Task Given_ADispatchingMeltWhosePaymentNeverStarted_When_CheckOutgoingPayment_Then_Unknown()
    {
        // Arrange: a crash between the quote's save and the payment's (the payment service has no row)
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var quote = new Domain.Cashu.Models.CashuQuoteModel("melt-crash", CashuQuoteMethod.Bolt11,
                                                            CashuQuoteDirection.Outgoing,
                                                            LightningMoney.Satoshis(1_000), DateTimeOffset.UtcNow)
        {
            PaymentHash = hash,
            Request = bolt11
        };
        quote.SetState(CashuQuoteState.Dispatching, DateTimeOffset.UtcNow);
        Quotes.Add(quote);

        // Act
        var response = await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hashHex }
        }, cancellationToken: Ct);

        // Assert: never sent, so the mint may try again (CDK's own BOLT 11 convention)
        Assert.Equal(QuoteState.Unknown, response.Status);
    }

    [Fact]
    public async Task Given_APartialMelt_When_MakePayment_Then_InvalidArgument()
    {
        // Arrange
        var (bolt11, _) = SignedBolt11(LightningMoney.Satoshis(1_000));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.MakePaymentAsync(
            new MakePaymentRequest
            {
                PaymentOptions = new OutgoingPaymentVariant
                {
                    Bolt11 = new Bolt11OutgoingPaymentOptions { Bolt11 = bolt11, QuoteId = "q" }
                },
                PartialAmount = new AmountMessage { Value = 500, Unit = "sat" }
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        PaymentService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AnInvalidInvoice_When_MakePayment_Then_InvalidArgument()
    {
        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.MakePaymentAsync(
            new MakePaymentRequest
            {
                PaymentOptions = new OutgoingPaymentVariant
                {
                    Bolt11 = new Bolt11OutgoingPaymentOptions { Bolt11 = "lnbcrt1notaninvoice", QuoteId = "q" }
                }
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        PaymentService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ASettledInvoiceOfTheMint_When_CheckedAndStreamed_Then_ReceivedInWholeSats()
    {
        // Arrange: 2,000,999 msat received floors to 2,000 sat
        var hash = Hash(0x02);
        var settled = InvoiceRow(hash, InvoiceStatus.Settled, LightningMoney.MilliSatoshis(2_000_999));
        settled.Label = MintLabel;
        InvoiceService.Setup(s => s.GetInvoiceAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(settled);
        var other = Hash(0x03);
        var notOurs = InvoiceRow(other, InvoiceStatus.Settled, LightningMoney.Satoshis(7));
        InvoiceService.Setup(s => s.GetInvoiceAsync(other, It.IsAny<CancellationToken>())).ReturnsAsync(notOurs);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();

        // Act
        var check = await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hash.ToString() }
        }, cancellationToken: Ct);
        Hub.Publish(new InvoiceSettledEvent(other, LightningMoney.Satoshis(7), DateTimeOffset.UtcNow));
        Hub.Publish(new InvoiceSettledEvent(hash, LightningMoney.MilliSatoshis(2_000_999), DateTimeOffset.UtcNow));

        // Assert: the check finds it; the stream skips the invoice without the mint's label
        var received = Assert.Single(check.Payments);
        Assert.Equal(2_000UL, received.PaymentAmount.Value);
        Assert.Equal(hash.ToString(), received.PaymentId);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        Assert.Equal(hash.ToString(), stream.ResponseStream.Current.PaymentReceived.PaymentIdentifier.Hash);
    }

    [Fact]
    public async Task Given_AnUnknownPayment_When_CheckOutgoingPayment_Then_Unknown()
    {
        // Act
        var response = await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier
            {
                Type = PaymentIdentifierType.PaymentHash,
                Hash = Hash(0x04).ToString()
            }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(QuoteState.Unknown, response.Status);
        Assert.Equal(0UL, response.TotalSpent.Value);
    }

    [Fact]
    public async Task Given_AMalformedOfferIdentifier_When_CheckIncomingPayment_Then_InvalidArgument()
    {
        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.CheckIncomingPaymentAsync(
            new CheckIncomingPaymentRequest
            {
                RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.OfferId, Id = "x" }
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    /// <summary>A regtest BOLT 11 invoice signed by a throwaway key, with its payment hash in hex.</summary>
    private static (string Bolt11, string HashHex) SignedBolt11(LightningMoney amount)
    {
        var invoice = new Invoice(amount, "melt", new uint256(RandomUtils.GetBytes(32)),
                                  new uint256(RandomUtils.GetBytes(32)), BitcoinNetwork.Regtest);
        var bolt11 = invoice.Encode(new Key());
        return (bolt11, Invoice.Decode(bolt11, BitcoinNetwork.Regtest).PaymentHash!.ToString());
    }

    private static InvoiceModel InvoiceRow(Hash hash, InvoiceStatus status, LightningMoney? received = null) =>
        new(hash, Preimage, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), LightningMoney.Satoshis(1_000),
            "mint", "lnbcrt1test", DateTimeOffset.UtcNow, 600, 40, status, received,
            status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null);

    private static PaymentModel Payment(Hash hash, LightningMoney amount, LightningMoney fee) =>
        new(hash, "lnbcrt1pay", Payee, amount, fee, DateTimeOffset.UtcNow);
}