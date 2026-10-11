using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Daemon.Tests.Cashu;

using Bolt11.Models;
using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.ValueObjects;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Cashu.PaymentProcessor.Grpc;

/// <summary>
/// What the CDK payment processor must never tell or do for a mint (wip/fafo integration review): a FAILED that is
/// not final (NL-999, NL-1001), another payment or invoice of the node (NL-999), melts or fees above the caps
/// (NL-1004), dropped events (NL-999) and malformed mint quotes (NL-999).
/// </summary>
public sealed class CdkPaymentProcessorSafetyTests : CdkProcessorTestBase
{
    [Fact]
    public async Task Given_APaymentNotOfTheMint_When_CheckOutgoingPayment_Then_UnknownWithoutItsPreimage()
    {
        // Arrange: the operator's own payment, and a trampoline relay's leg that carries the mint's label
        var own = Payment(Hash(0x05), LightningMoney.Satoshis(1_000), null);
        own.Succeed(Preimage, DateTimeOffset.UtcNow);
        var relay = PaymentModel.Restore(Hash(0x06), "lnbcrt1pay", Payee, LightningMoney.Satoshis(1_000),
                                         LightningMoney.Zero, DateTimeOffset.UtcNow, PaymentStatus.Succeeded, null,
                                         null, Preimage, null, null, null, DateTimeOffset.UtcNow,
                                         isTrampolineRelay: true);
        relay.Label = MintLabel;
        PaymentService.Setup(s => s.GetPaymentAsync(own.PaymentHash, It.IsAny<CancellationToken>())).ReturnsAsync(own);
        PaymentService.Setup(s => s.GetPaymentAsync(relay.PaymentHash, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(relay);

        // Act
        var ownCheck = await CheckOutgoingAsync(own.PaymentHash);
        var relayCheck = await CheckOutgoingAsync(relay.PaymentHash);

        // Assert
        Assert.All([ownCheck, relayCheck], r =>
        {
            Assert.Equal(QuoteState.Unknown, r.Status);
            Assert.Empty(r.PaymentProof);
            Assert.Equal(0UL, r.TotalSpent.Value);
        });
    }

    [Fact]
    public async Task Given_AMeltBetweenTwoAttempts_When_CheckOutgoingPayment_Then_PendingUntilTheRetriesEnd()
    {
        // Arrange (NL-999): the stored row reads Failed while the payment service still retries the payment
        var hash = Hash(0x07);
        var payment = Payment(hash, LightningMoney.Satoshis(1_000), MintLabel);
        payment.Fail(null, null, "Retrying.", DateTimeOffset.UtcNow);
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        PaymentService.Setup(s => s.IsPaying(hash)).Returns(true);

        // Act
        var retrying = await CheckOutgoingAsync(hash);
        PaymentService.Setup(s => s.IsPaying(hash)).Returns(false);
        var final = await CheckOutgoingAsync(hash);

        // Assert
        Assert.Equal(QuoteState.Pending, retrying.Status);
        Assert.Equal(QuoteState.Failed, final.Status);
    }

    [Fact]
    public async Task Given_AMeltFailedForAnUnknownOutcomeAtStartup_When_CheckedAndMade_Then_UnknownNeverFailed()
    {
        // Arrange (NL-1001): failed at startup because its HTLCs were gone; a replayed fulfill may still prove it paid
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var payment = Payment(hash, LightningMoney.Satoshis(1_000), MintLabel);
        payment.Fail(null, null, PaymentModel.UnknownOutcomeReason, DateTimeOffset.UtcNow);
        Assert.True(payment.IsOutcomeUnknown);
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        PaymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new PayInvoiceResult(payment, 1, 1));

        // Act
        var check = await CheckOutgoingAsync(hash);
        var made = await MeltAsync(bolt11, "melt-unknown");

        // Assert
        Assert.Equal(QuoteState.Unknown, check.Status);
        Assert.Equal(QuoteState.Unknown, made.Status);
        Assert.NotEqual(Domain.Cashu.Enums.CashuQuoteState.Failed, (await Quotes.GetAsync("melt-unknown"))!.State);
    }

    [Fact]
    public async Task Given_AnInvoiceThisNodePaidOutsideTheMint_When_MakePayment_Then_FailedPreconditionNotPaid()
    {
        // Arrange (NL-999): the payment service refuses a hash it paid; the stored payment is the operator's
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var own = Payment(hash, LightningMoney.Satoshis(1_000), null);
        own.Succeed(Preimage, DateTimeOffset.UtcNow);
        PaymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException("Already paid."));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(own);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await MeltAsync(bolt11, "melt-2"));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.DoesNotContain(Convert.ToHexString((byte[])Preimage), error.Status.Detail,
                              StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Domain.Cashu.Enums.CashuQuoteState.Failed, (await Quotes.GetAsync("melt-2"))!.State);
    }

    [Fact]
    public async Task Given_ASettledInvoiceNotOfTheMint_When_CheckIncomingPayment_Then_NoPayment()
    {
        // Arrange (NL-999)
        var hash = Hash(0x08);
        InvoiceService.Setup(s => s.GetInvoiceAsync(hash, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(InvoiceRow(hash, InvoiceStatus.Settled, null));

        // Act
        var check = await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hash.ToString() }
        }, cancellationToken: Ct);

        // Assert
        Assert.Empty(check.Payments);
    }

    [Fact]
    public async Task Given_AMeltAboveTheCap_When_QuotedOrMade_Then_RefusedBeforeAnyPayment()
    {
        // Arrange (NL-1004): the default cap is 1,000,000 sat
        var (bolt11, _) = SignedBolt11(LightningMoney.Satoshis(1_000_001));

        // Act
        var quote = await Assert.ThrowsAsync<RpcException>(async () => await Client.GetPaymentQuoteAsync(
                        new PaymentQuoteRequest
                        {
                            Request = bolt11,
                            Unit = "sat",
                            RequestType = OutgoingPaymentRequestType.Bolt11Invoice
                        }, cancellationToken: Ct));
        var melt = await Assert.ThrowsAsync<RpcException>(async () => await MeltAsync(bolt11, "melt-big"));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, quote.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, melt.StatusCode);
        Assert.Contains("1000000 sat", melt.Status.Detail);
        PaymentService.Verify(s => s.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                     It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_AMintAskingForAHugeFeeLimit_When_MakePayment_Then_TheLimitIsCapped()
    {
        // Arrange (NL-1004): 1 % of 100,000 sat is 1,000 sat; the mint asks for 50,000 sat
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(100_000));
        var payment = Payment(new Hash(Convert.FromHexString(hashHex)), LightningMoney.Satoshis(100_000), MintLabel);
        PayInvoiceOptions? used = null;
        PaymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .Callback<string, LightningMoney?, PayInvoiceOptions, CancellationToken>((_, _, o, _) => used = o)
                      .ReturnsAsync(new PayInvoiceResult(payment, 1, 1));

        // Act
        await MeltAsync(bolt11, "melt-fee", new AmountMessage { Value = 50_000, Unit = "sat" });

        // Assert
        Assert.Equal(LightningMoney.Satoshis(1_000), used!.MaxFee);
    }

    [Theory]
    [InlineData(ulong.MaxValue, false)]
    [InlineData(1_000UL, true)]
    public async Task Given_AnAmountOrDescriptionOutOfRange_When_CreatePayment_Then_InvalidArgument(ulong sat,
        bool serviceRefuses)
    {
        // Arrange (NL-999): an amount that overflows msat, or an invoice the service refuses
        InvoiceService.Setup(s => s.CreateInvoiceAsync(It.IsAny<LightningMoney?>(), It.IsAny<string>(),
                                                       It.IsAny<uint?>(), It.IsAny<SourceLabels>(),
                                                       It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new ArgumentException("The description is too long."));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                Options = new IncomingPaymentOptions
                {
                    Bolt11 = new Bolt11IncomingPaymentOptions
                    {
                        Amount = new AmountMessage { Value = sat, Unit = "sat" },
                        Description = serviceRefuses ? new string('x', 700) : "mint"
                    }
                }
            }, cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Given_TheProcessorMissedNodeEvents_When_AMintStreams_Then_TheStreamEndsUnavailable()
    {
        // Arrange (NL-999): the processor's own subscription overflows (capacity 1 here), so a mint's stream must end
        var source = new Mock<IPaymentEventSource>();
        var subscription = new Mock<IPaymentEventSubscription>();
        subscription.SetupGet(s => s.Overflowed).Returns(true);
        subscription.Setup(s => s.ReadAllAsync(It.IsAny<CancellationToken>()))
                    .Returns(Events(new InvoiceSettledEvent(Hash(0x09), LightningMoney.Satoshis(1),
                                                            DateTimeOffset.UtcNow)));
        source.Setup(s => s.Subscribe(It.IsAny<int>())).Returns(subscription.Object);
        await using var service = new CdkPaymentProcessorService(
            InvoiceService.Object, PaymentService.Object, source.Object,
            Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            Options.Create(new CashuPaymentProcessorOptions { Enabled = true }),
            NullLogger<CdkPaymentProcessorService>.Instance);
        var writer = new Mock<IServerStreamWriter<PaymentEventResponse>>();
        var context = new Mock<ServerCallContext>();
        var stream = service.WaitPaymentEvent(new EmptyRequest(), writer.Object, context.Object);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.StreamCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);

        // Act
        await service.StartBackgroundAsync(Ct);

        // Assert
        var error = await Assert.ThrowsAsync<RpcException>(() => stream.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AMeltPendingAtTheTimeout_When_AnAttemptFailsWhileRetrying_Then_TheStreamSaysNothing()
    {
        // Arrange (NL-999): the melt answered pending; then an attempt fails while the payment service retries
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(2_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var payment = Payment(hash, LightningMoney.Satoshis(2_000), MintLabel);
        PaymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new PayInvoiceResult(payment, 1, 1));
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        Assert.Equal(QuoteState.Pending, (await MeltAsync(bolt11, "melt-retrying")).Status);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();

        // Act: a failure while retrying, then the success. NL-985: the success's row is served only once the
        // processor read the failed one; switched before, the failure read the success and recorded the quote Paid,
        // so the success event found nothing left to report (the test failed whenever its processor was fast)
        payment.Fail(null, null, "Retrying.", DateTimeOffset.UtcNow);
        PaymentService.Setup(s => s.IsPaying(hash)).Returns(true);
        var failureRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>()))
                      .Callback(() => failureRead.TrySetResult())
                      .ReturnsAsync(payment);
        Hub.Publish(new PaymentFailedEvent(hash, "Retrying.", DateTimeOffset.UtcNow));
        await failureRead.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var succeeded = Payment(hash, LightningMoney.Satoshis(2_000), MintLabel);
        succeeded.Succeed(Preimage, DateTimeOffset.UtcNow);
        PaymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(succeeded);
        Hub.Publish(new PaymentSucceededEvent(hash, succeeded.Amount, succeeded.Fee, Preimage, DateTimeOffset.UtcNow));

        // Assert: the stream's first message is the success
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        Assert.Equal("melt-retrying", stream.ResponseStream.Current.PaymentSuccessful.QuoteId);
    }

    [Fact]
    public async Task Given_TooManyEventStreams_When_OneMoreSubscribes_Then_ResourceExhausted()
    {
        // Arrange (NL-1000): at most 4 streams by default
        var streams = Enumerable.Range(0, 4)
                                .Select(_ => Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct))
                                .ToList();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Service.StreamCount < 4 && DateTime.UtcNow < deadline)
                await Task.Delay(10, Ct);

            // Act
            using var extra = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
            var error = await Assert.ThrowsAsync<RpcException>(() => extra.ResponseStream.MoveNext(Bounded));

            // Assert
            Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
        }
        finally
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    [Fact]
    public async Task Given_TheMeltLimitReached_When_OneMoreMeltComes_Then_ResourceExhausted()
    {
        // Arrange (NL-1000): one melt at a time, the first one held by the payment service
        await using var provider = BuildProvider(new CashuPaymentProcessorOptions
        {
            Enabled = true,
            Port = 0,
            AllowInsecureLoopback = true,
            MaxConcurrentMelts = 1
        });
        var service = provider.GetRequiredService<CdkPaymentProcessorService>();
        var held = new TaskCompletionSource<PayInvoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        PaymentService.Setup(s => s.PayInvoiceAsync(It.IsAny<string>(), null, It.IsAny<PayInvoiceOptions>(),
                                                    It.IsAny<CancellationToken>()))
                      .Returns(held.Task);
        var (first, firstHash) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var (second, _) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var context = new Mock<ServerCallContext>().Object;

        // Act
        var pending = service.MakePayment(Bolt11Melt(first, "melt-held"), context);
        var error = await Assert.ThrowsAsync<RpcException>(() => service.MakePayment(Bolt11Melt(second, "melt-2nd"),
                                                                                     context));
        held.SetResult(new PayInvoiceResult(Payment(new Hash(Convert.FromHexString(firstHash)),
                                                    LightningMoney.Satoshis(1_000), MintLabel), 1, 1));
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    private static MakePaymentRequest Bolt11Melt(string bolt11, string quoteId) => new()
    {
        PaymentOptions = new OutgoingPaymentVariant
        {
            Bolt11 = new Bolt11OutgoingPaymentOptions { Bolt11 = bolt11, QuoteId = quoteId }
        }
    };

    private async Task<MakePaymentResponse> CheckOutgoingAsync(Hash hash) =>
        await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hash.ToString() }
        }, cancellationToken: Ct);

    private async Task<MakePaymentResponse> MeltAsync(string bolt11, string quoteId, AmountMessage? maxFee = null)
    {
        var options = new Bolt11OutgoingPaymentOptions { Bolt11 = bolt11, QuoteId = quoteId };
        if (maxFee is not null)
            options.MaxFeeAmount = maxFee;
        return await Client.MakePaymentAsync(new MakePaymentRequest
        {
            PaymentOptions = new OutgoingPaymentVariant { Bolt11 = options }
        }, cancellationToken: Ct);
    }

    private static async IAsyncEnumerable<PaymentEvent> Events(params PaymentEvent[] events)
    {
        foreach (var paymentEvent in events)
        {
            await Task.Yield();
            yield return paymentEvent;
        }
    }

    /// <summary>A regtest BOLT 11 invoice signed by a throwaway key, with its payment hash in hex.</summary>
    private static (string Bolt11, string HashHex) SignedBolt11(LightningMoney amount)
    {
        var invoice = new Invoice(amount, "melt", new uint256(RandomUtils.GetBytes(32)),
                                  new uint256(RandomUtils.GetBytes(32)), BitcoinNetwork.Regtest);
        var bolt11 = invoice.Encode(new Key());
        return (bolt11, Invoice.Decode(bolt11, BitcoinNetwork.Regtest).PaymentHash!.ToString());
    }

    private static InvoiceModel InvoiceRow(Hash hash, InvoiceStatus status, string? label) =>
        new(hash, Preimage, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), LightningMoney.Satoshis(1_000),
            "mint", "lnbcrt1test", DateTimeOffset.UtcNow, 600, 40, status, LightningMoney.Satoshis(1_000),
            status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null)
        { Label = label };

    private static PaymentModel Payment(Hash hash, LightningMoney amount, string? label) =>
        new(hash, "lnbcrt1pay", Payee, amount, LightningMoney.Zero, DateTimeOffset.UtcNow) { Label = label };
}