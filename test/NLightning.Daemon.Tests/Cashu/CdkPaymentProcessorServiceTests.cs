using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// The CDK payment processor (Cashu plan C1, NL-992) over a real Kestrel gRPC server on loopback, called with the
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
        services.AddSingleton(Options.Create(new CashuPaymentProcessorOptions
        {
            Enabled = true,
            Port = 0,
            AllowInsecureLoopback = true
        }));
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
    public async Task Given_AMelt_When_MakePaymentSucceeds_Then_PaidWithProofAndTheQuoteIsNotStreamedAgain()
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
        var minted = Hash(0x0a);
        var mintInvoice = InvoiceRow(minted, InvoiceStatus.Settled, LightningMoney.Satoshis(3));
        mintInvoice.Label = "cashu-mint";
        _invoiceService.Setup(s => s.GetInvoiceAsync(minted, It.IsAny<CancellationToken>())).ReturnsAsync(mintInvoice);
        _hub.Publish(new InvoiceSettledEvent(minted, LightningMoney.Satoshis(3), DateTimeOffset.UtcNow));

        // Assert: paid, 1,001.5 sat spent rounds up to 1,002, the fee limit and the label went to the payment
        Assert.Equal(QuoteState.Paid, response.Status);
        Assert.Equal(1_002UL, response.TotalSpent.Value);
        Assert.Equal(Convert.ToHexString((byte[])s_preimage).ToLowerInvariant(), response.PaymentProof);
        Assert.Equal(LightningMoney.Satoshis(5), usedOptions!.MaxFee);
        Assert.Equal("cashu-mint", usedOptions.Labels.Label);
        // The final answer resolved the quote, so its quote id is forgotten (NL-999): the stream's next message is the
        // mint's settled invoice, not the melt
        Assert.True(await stream.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        Assert.Equal(minted.ToString(), stream.ResponseStream.Current.PaymentReceived.PaymentIdentifier.Hash);
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

    [Fact]
    public async Task Given_APaymentNotOfTheMint_When_CheckOutgoingPayment_Then_UnknownWithoutItsPreimage()
    {
        // Arrange (NL-999): the operator's own payment, and a trampoline relay's leg with the mint's label
        var own = Payment(Hash(0x05), LightningMoney.Satoshis(1_000), LightningMoney.Zero);
        own.Succeed(s_preimage, DateTimeOffset.UtcNow);
        var relay = PaymentModel.Restore(Hash(0x06), "lnbcrt1pay", s_payee, LightningMoney.Satoshis(1_000),
                                         LightningMoney.Zero, DateTimeOffset.UtcNow, PaymentStatus.Succeeded, null,
                                         null, s_preimage, null, null, null, DateTimeOffset.UtcNow,
                                         isTrampolineRelay: true);
        relay.Label = "cashu-mint";
        _paymentService.Setup(s => s.GetPaymentAsync(own.PaymentHash, It.IsAny<CancellationToken>())).ReturnsAsync(own);
        _paymentService.Setup(s => s.GetPaymentAsync(relay.PaymentHash, It.IsAny<CancellationToken>()))
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
    public async Task Given_AMeltBetweenTwoAttempts_When_CheckOutgoingPayment_Then_PendingNotFailed()
    {
        // Arrange (NL-999): the stored row reads Failed while the payment service still retries
        var hash = Hash(0x07);
        var payment = Payment(hash, LightningMoney.Satoshis(1_000), LightningMoney.Zero);
        payment.Label = "cashu-mint";
        payment.Fail(null, null, "Retrying.", DateTimeOffset.UtcNow);
        _paymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(payment);
        _paymentService.Setup(s => s.IsPaying(hash)).Returns(true);

        // Act
        var retrying = await CheckOutgoingAsync(hash);
        _paymentService.Setup(s => s.IsPaying(hash)).Returns(false);
        var final = await CheckOutgoingAsync(hash);

        // Assert
        Assert.Equal(QuoteState.Pending, retrying.Status);
        Assert.Equal(QuoteState.Failed, final.Status);
    }

    [Fact]
    public async Task Given_AnInvoiceThisNodePaidOutsideTheMint_When_MakePayment_Then_FailedPreconditionNotPaid()
    {
        // Arrange (NL-999): the payment service refuses a hash it paid; the stored payment is the operator's
        var (bolt11, hashHex) = SignedBolt11(LightningMoney.Satoshis(1_000));
        var hash = new Hash(Convert.FromHexString(hashHex));
        var own = Payment(hash, LightningMoney.Satoshis(1_000), LightningMoney.Zero);
        own.Succeed(s_preimage, DateTimeOffset.UtcNow);
        _paymentService.Setup(s => s.PayInvoiceAsync(bolt11, null, It.IsAny<PayInvoiceOptions>(),
                                                     It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("Already paid."));
        _paymentService.Setup(s => s.GetPaymentAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(own);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Client.MakePaymentAsync(
            new MakePaymentRequest
            {
                PaymentOptions = new OutgoingPaymentVariant
                {
                    Bolt11 = new Bolt11OutgoingPaymentOptions { Bolt11 = bolt11, QuoteId = "melt-2" }
                }
            }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.DoesNotContain(Convert.ToHexString((byte[])s_preimage), error.Status.Detail,
                              StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Given_ASettledInvoiceNotOfTheMint_When_CheckIncomingPayment_Then_NoPayment()
    {
        // Arrange (NL-999)
        var hash = Hash(0x08);
        _invoiceService.Setup(s => s.GetInvoiceAsync(hash, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(InvoiceRow(hash, InvoiceStatus.Settled, LightningMoney.Satoshis(1_000)));

        // Act
        var check = await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hash.ToString() }
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(check.Payments);
    }

    [Theory]
    [InlineData(ulong.MaxValue, false)]
    [InlineData(1_000UL, true)]
    public async Task Given_AnAmountOrDescriptionOutOfRange_When_CreatePayment_Then_InvalidArgument(ulong sat,
        bool serviceRefuses)
    {
        // Arrange (NL-999): an amount that overflows msat, or an invoice the service refuses
        _invoiceService.Setup(s => s.CreateInvoiceAsync(It.IsAny<LightningMoney?>(), It.IsAny<string>(),
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
            }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Given_AnOverflowedSubscription_When_TheStreamReadsItsNextEvent_Then_ItEndsUnavailable()
    {
        // Arrange (NL-999): dropped events end the stream, so the mint subscribes again and checks its quotes
        var subscription = new Mock<IPaymentEventSubscription>();
        subscription.SetupGet(s => s.Overflowed).Returns(true);
        subscription.Setup(s => s.ReadAllAsync(It.IsAny<CancellationToken>()))
                    .Returns(Events(new InvoiceSettledEvent(Hash(0x09), LightningMoney.Satoshis(1),
                                                            DateTimeOffset.UtcNow)));
        var source = new Mock<IPaymentEventSource>();
        source.Setup(s => s.Subscribe(It.IsAny<int>())).Returns(subscription.Object);
        var service = new CdkPaymentProcessorService(_invoiceService.Object, _paymentService.Object, source.Object,
                                                     Options.Create(new NodeOptions()),
                                                     Options.Create(new CashuPaymentProcessorOptions()),
                                                     Microsoft.Extensions.Logging.Abstractions.NullLogger<
                                                         CdkPaymentProcessorService>.Instance);
        var writer = new Mock<IServerStreamWriter<PaymentEventResponse>>();

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => service.WaitPaymentEvent(
                                                                new EmptyRequest(), writer.Object,
                                                                new Mock<ServerCallContext>().Object));

        // Assert
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        writer.VerifyNoOtherCalls();
        subscription.Verify(s => s.Dispose());
    }

    [Fact]
    public async Task Given_AnAmbientKestrelEndpoint_When_TheHostStarts_Then_ItListensOnTheConfiguredAddressOnly()
    {
        // Arrange (NL-998): an endpoint from the environment must not open a second, unchecked listener
        const string variable = "Kestrel__Endpoints__Ambient__Url";
        Environment.SetEnvironmentVariable(variable, "http://127.0.0.1:0");
        try
        {
            // Act
            var addresses = _host!.ListeningAddresses;
            await using var provider = new ServiceCollection()
                                       .AddLogging()
                                       .AddSingleton(_invoiceService.Object)
                                       .AddSingleton(_paymentService.Object)
                                       .AddSingleton<IPaymentEventSource>(_hub)
                                       .AddSingleton(Options.Create(new NodeOptions
                                       {
                                           BitcoinNetwork = BitcoinNetwork.Regtest
                                       }))
                                       .AddSingleton(Options.Create(new CashuPaymentProcessorOptions
                                       {
                                           Enabled = true,
                                           Port = 0,
                                           AllowInsecureLoopback = true
                                       }))
                                       .AddSingleton<CdkPaymentProcessorService>()
                                       .AddSingleton<CashuPaymentProcessorHost>()
                                       .BuildServiceProvider();
            var host = provider.GetRequiredService<CashuPaymentProcessorHost>();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var ambient = host.ListeningAddresses;
            await host.StopAsync(CancellationToken.None);

            // Assert
            Assert.StartsWith("http://127.0.0.1:", Assert.Single(addresses));
            Assert.StartsWith("http://127.0.0.1:", Assert.Single(ambient));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Given_ClientCertificates_When_CheckedAgainstTheMintCa_Then_OnlyClientCertificatesOfThatCaPass()
    {
        // Arrange (NL-998)
        using var ca = Authority("nltg-test-ca");
        using var otherCa = Authority("nltg-other-ca");
        using var client = Issue(ca, "client", "1.3.6.1.5.5.7.3.2");
        using var server = Issue(ca, "server", "1.3.6.1.5.5.7.3.1");
        using var stranger = Issue(otherCa, "stranger", "1.3.6.1.5.5.7.3.2");

        // Act & Assert
        Assert.True(CashuPaymentProcessorHost.IsSignedBy(client, ca));
        Assert.False(CashuPaymentProcessorHost.IsSignedBy(server, ca));
        Assert.False(CashuPaymentProcessorHost.IsSignedBy(stranger, ca));
        Assert.False(CashuPaymentProcessorHost.IsSignedBy(otherCa, ca));
    }

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Issue(X509Certificate2 ca, string name, string eku)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10),
                                          RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }

    private async Task<MakePaymentResponse> CheckOutgoingAsync(Hash hash) =>
        await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.PaymentHash, Hash = hash.ToString() }
        }, cancellationToken: TestContext.Current.CancellationToken);

    private static async IAsyncEnumerable<PaymentEvent> Events(params PaymentEvent[] events)
    {
        foreach (var paymentEvent in events)
        {
            await Task.Yield();
            yield return paymentEvent;
        }
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