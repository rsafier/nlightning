using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Daemon.Tests.Cashu;

using Application.Payments.Events;
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
/// The CDK payment processor (Cashu plan C1, NL-902) over a real Kestrel gRPC server on loopback, called with the
/// client generated from CDK's own proto, with the node's invoice and payment services mocked.
/// </summary>
public sealed class CdkPaymentProcessorServiceTests : IAsyncLifetime
{
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);

    private readonly PaymentEventHub _hub = new();
    private readonly Mock<IInvoiceService> _invoiceService = new();
    private readonly Mock<IPaymentService> _paymentService = new();
    private ServiceProvider? _provider;
    private CashuPaymentProcessorHost? _host;
    private GrpcChannel? _channel;

    private CdkPaymentProcessor.CdkPaymentProcessorClient Client => new(_channel!);

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_invoiceService.Object);
        services.AddSingleton(_paymentService.Object);
        services.AddSingleton<IPaymentEventSource>(_hub);
        services.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        services.AddSingleton(Options.Create(new CashuPaymentProcessorOptions { Enabled = true, Port = 0 }));
        services.AddSingleton<CdkPaymentProcessorService>();
        services.AddSingleton<CashuPaymentProcessorHost>();
        _provider = services.BuildServiceProvider();
        _host = _provider.GetRequiredService<CashuPaymentProcessorHost>();
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_host.BoundPort}");
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        if (_provider is not null)
            await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Given_TheProcessor_When_GetSettings_Then_Bolt11OnlyInSat()
    {
        // Act
        var settings = await Client.GetSettingsAsync(new EmptyRequest(),
                                                     cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("sat", settings.Unit);
        Assert.NotNull(settings.Bolt11);
        Assert.True(settings.Bolt11.Amountless);
        Assert.False(settings.Bolt11.Mpp);
        Assert.Null(settings.Bolt12);
        Assert.Null(settings.Onchain);
    }

    [Fact]
    public async Task Given_ABolt11MintQuote_When_CreatePayment_Then_AnInvoiceLabelledForTheMintIsCreated()
    {
        // Arrange
        var hash = Hash(0x01);
        _invoiceService.Setup(s => s.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), "mint", null,
                                                        It.Is<SourceLabels>(l => l.Label == "cashu-mint"),
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
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PaymentIdentifierType.PaymentHash, response.RequestIdentifier.Type);
        Assert.Equal(hash.ToString(), response.RequestIdentifier.Hash);
        Assert.Equal("lnbcrt1test", response.Request);
        Assert.True(response.Expiry > 0);
    }

    [Fact]
    public async Task Given_ABolt12MintQuote_When_CreatePayment_Then_Unimplemented()
    {
        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                Options = new IncomingPaymentOptions { Bolt12 = new Bolt12IncomingPaymentOptions() }
            }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
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
        }, cancellationToken: TestContext.Current.CancellationToken);

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
            }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Given_AMelt_When_MakePaymentSucceeds_Then_PaidWithProofAndTheStreamNamesTheQuote()
    {
        // Arrange
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var payment = Payment(hash, LightningMoney.Satoshis(1_000), LightningMoney.MilliSatoshis(1_500));
        payment.Succeed(s_preimage, DateTimeOffset.UtcNow);
        PayInvoiceOptions? usedOptions = null;
        _paymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                     It.IsAny<CancellationToken>()))
                       .Callback<string, LightningMoney?, PayInvoiceOptions, CancellationToken>(
                            (_, _, o, _) => usedOptions = o)
                       .ReturnsAsync(new PayInvoiceResult(payment, 1, 1));
        _paymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(),
                                                   cancellationToken: TestContext.Current.CancellationToken);
        await WaitForSubscriberAsync();

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
        }, cancellationToken: TestContext.Current.CancellationToken);
        _hub.Publish(new PaymentSucceededEvent(hash, payment.Amount, payment.Fee, s_preimage, DateTimeOffset.UtcNow));

        // Assert: paid, 1,001.5 sat spent rounds up to 1,002, the fee limit and the label went to the payment
        Assert.Equal(QuoteState.Paid, response.Status);
        Assert.Equal(1_002UL, response.TotalSpent.Value);
        Assert.Equal(Convert.ToHexString((byte[])s_preimage).ToLowerInvariant(), response.PaymentProof);
        Assert.Equal(LightningMoney.Satoshis(5), usedOptions!.MaxFee);
        Assert.Equal("cashu-mint", usedOptions.Labels.Label);
        Assert.True(await stream.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        Assert.Equal("melt-1", stream.ResponseStream.Current.PaymentSuccessful.QuoteId);
        Assert.Equal(QuoteState.Paid, stream.ResponseStream.Current.PaymentSuccessful.Details.Status);
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
            }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        _paymentService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ASettledInvoiceOfTheMint_When_CheckedAndStreamed_Then_ReceivedInWholeSats()
    {
        // Arrange: 2,000,999 msat received floors to 2,000 sat
        var hash = Hash(0x02);
        var settled = InvoiceRow(hash, InvoiceStatus.Settled, LightningMoney.MilliSatoshis(2_000_999));
        settled.Label = "cashu-mint";
        _invoiceService.Setup(s => s.GetInvoiceAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(settled);
        var other = Hash(0x03);
        var notOurs = InvoiceRow(other, InvoiceStatus.Settled, LightningMoney.Satoshis(7));
        _invoiceService.Setup(s => s.GetInvoiceAsync(other, It.IsAny<CancellationToken>())).ReturnsAsync(notOurs);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(),
                                                   cancellationToken: TestContext.Current.CancellationToken);
        await WaitForSubscriberAsync();

        // Act
        var check = await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hash.ToString() }
        }, cancellationToken: TestContext.Current.CancellationToken);
        _hub.Publish(new InvoiceSettledEvent(other, LightningMoney.Satoshis(7), DateTimeOffset.UtcNow));
        _hub.Publish(new InvoiceSettledEvent(hash, LightningMoney.MilliSatoshis(2_000_999), DateTimeOffset.UtcNow));

        // Assert: the check finds it; the stream skips the invoice without the mint's label
        var received = Assert.Single(check.Payments);
        Assert.Equal(2_000UL, received.PaymentAmount.Value);
        Assert.Equal(hash.ToString(), received.PaymentId);
        Assert.True(await stream.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
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
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(QuoteState.Unknown, response.Status);
        Assert.Equal(0UL, response.TotalSpent.Value);
    }

    [Fact]
    public async Task Given_AnOfferIdentifier_When_CheckIncomingPayment_Then_InvalidArgument()
    {
        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.CheckIncomingPaymentAsync(
            new CheckIncomingPaymentRequest
            {
                RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.OfferId, Id = "x" }
            }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    private async Task WaitForSubscriberAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_hub.SubscriberCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, _hub.SubscriberCount);
    }

    private static Hash Hash(byte b) => new(Enumerable.Repeat(b, 32).ToArray());

    /// <summary>A regtest BOLT 11 invoice signed by a throwaway key, with its payment hash in hex.</summary>
    private static (string Bolt11, string HashHex) SignedBolt11(LightningMoney amount)
    {
        var invoice = new Invoice(amount, "melt", new uint256(RandomUtils.GetBytes(32)),
                                  new uint256(RandomUtils.GetBytes(32)), BitcoinNetwork.Regtest);
        var bolt11 = invoice.Encode(new Key());
        return (bolt11, Invoice.Decode(bolt11, BitcoinNetwork.Regtest).PaymentHash!.ToString());
    }

    private static InvoiceModel InvoiceRow(Hash hash, InvoiceStatus status, LightningMoney? received = null) =>
        new(hash, s_preimage, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), LightningMoney.Satoshis(1_000),
            "mint", "lnbcrt1test", DateTimeOffset.UtcNow, 600, 40, status, received,
            status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null);

    private static PaymentModel Payment(Hash hash, LightningMoney amount, LightningMoney fee) =>
        new(hash, "lnbcrt1pay", s_payee, amount, fee, DateTimeOffset.UtcNow);
}