using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Offers;

using Application.Gossip.Interfaces;
using Application.Offers.Send;
using Application.Payments.Invoices;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Send;

/// <summary>
/// BOLT 12 plan B4-T4, step 1 of lane B12-E: a node pays an offer end to end over <see cref="ThreeNodeHarness"/>
/// (Alice - Bob - Carol, production channels, switches, Sphinx, blinding and SQLite): the production
/// <see cref="OfferPaymentService"/> builds and signs the invoice_request, verifies the invoice and pays it with the
/// production <see cref="PaymentService.PayBlindedAsync"/> over Carol's blinded path (made by her production
/// <see cref="BlindedPathBuilder"/> for her real invoice). The onion-message hop to Carol is a test double
/// (<see cref="CarolOfferIssuer"/>) that answers like an issuer until lane B12-D's invoice_request handler is
/// integrated; the integration swaps it for the real onion messages.
/// </summary>
public class OfferHarnessTests
{
    private const ulong AmountMsat = 40_000_000;

    [Fact]
    public async Task Given_CarolsOffer_When_AlicePaysIt_Then_CarolSettlesAndAlicesPaymentHasTheBolt12Invoice()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var issuer = new CarolOfferIssuer();
        await using var harness = await CreateAsync(issuer, payers: ["Alice"]);
        issuer.Attach(harness);
        var offer = issuer.Offers.CreateOffer(amountMsat: AmountMsat, description: "harness offer",
                                              chain: ChainConstants.Regtest);
        var service = harness.Alice.Services.GetRequiredService<IOfferPaymentService>();

        // Act
        var paying = service.PayOfferAsync(new PayOfferRequest(offer, PayerNote: "from alice"),
                                           new PayOfferOptions(), ct);
        var result = await PumpUntilDoneAsync(harness, paying);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, result.Fetch.Status);
        Assert.NotNull(result.Payment);
        var payment = result.Payment!.Payment;
        Assert.True(payment.Status == PaymentStatus.Succeeded, payment.FailureReason);
        Assert.Equal(issuer.Offers.NodeId, payment.PayeeNodeId);
        Assert.Equal(AmountMsat, payment.Amount.MilliSatoshi);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(result.Fetch.Invoice!.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        Assert.Equal(stored.Preimage, payment.Preimage);
        Assert.Equal("from alice", issuer.LastPayerNote);
    }

    [Fact]
    public async Task Given_CarolsPathIntroducedByBob_When_BobPaysTheOffer_Then_HeSendsStraightToCarol()
    {
        // Arrange: B12-PAY-02, the payer is the introduction node of the invoice's path
        var ct = TestContext.Current.CancellationToken;
        var issuer = new CarolOfferIssuer();
        await using var harness = await CreateAsync(issuer, payers: ["Bob"]);
        issuer.Attach(harness);
        var offer = issuer.Offers.CreateOffer(amountMsat: AmountMsat, chain: ChainConstants.Regtest);
        var service = harness.Bob.Services.GetRequiredService<IOfferPaymentService>();

        // Act
        var paying = service.PayOfferAsync(new PayOfferRequest(offer), new PayOfferOptions(), ct);
        var result = await PumpUntilDoneAsync(harness, paying);

        // Assert
        var payment = result.Payment!.Payment;
        Assert.True(payment.Status == PaymentStatus.Succeeded, payment.FailureReason);
        var add = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage));
        Assert.NotNull(add.BlindedPathTlv);
        Assert.DoesNotContain(harness.Alice.Received, m => m is UpdateAddHtlcMessage);
    }

    [Fact]
    public async Task Given_CarolRestartsBetweenInvoiceAndPayment_When_AlicePays_Then_ThePaymentStillSettles()
    {
        // Arrange: Carol's invoice row is saved before the invoice goes out, so a restart loses nothing
        var ct = TestContext.Current.CancellationToken;
        var issuer = new CarolOfferIssuer();
        await using var harness = await CreateAsync(issuer, payers: ["Alice"]);
        issuer.Attach(harness);
        var offer = issuer.Offers.CreateOffer(amountMsat: AmountMsat, chain: ChainConstants.Regtest);
        var service = harness.Alice.Services.GetRequiredService<IOfferPaymentService>();
        var fetched = await service.FetchInvoiceAsync(new PayOfferRequest(offer), new PayOfferOptions(), ct);
        Assert.Equal(FetchInvoiceStatus.Received, fetched.Status);
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        issuer.ReplayLastInvoice = true;

        // Act
        var paying = service.PayOfferAsync(new PayOfferRequest(offer), new PayOfferOptions(), ct);
        var result = await PumpUntilDoneAsync(harness, paying);

        // Assert
        Assert.Equal(fetched.Invoice!.PaymentHash, result.Fetch.Invoice!.PaymentHash);
        Assert.True(result.Payment!.Payment.Status == PaymentStatus.Succeeded, result.Payment.Payment.FailureReason);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(fetched.Invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    [Fact]
    public async Task Given_TheIssuerRefuses_When_AlicePays_Then_TheInvoiceErrorIsReportedAndNothingPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var issuer = new CarolOfferIssuer { Refusal = "offer disabled" };
        await using var harness = await CreateAsync(issuer, payers: ["Alice"]);
        issuer.Attach(harness);
        var offer = issuer.Offers.CreateOffer(amountMsat: AmountMsat, chain: ChainConstants.Regtest);

        // Act
        var result = await harness.Alice.Services.GetRequiredService<IOfferPaymentService>()
                                  .PayOfferAsync(new PayOfferRequest(offer), new PayOfferOptions(), ct);

        // Assert
        Assert.Equal(FetchInvoiceStatus.InvoiceError, result.Fetch.Status);
        Assert.Equal("offer disabled", result.Fetch.Error);
        Assert.Null(result.Payment);
        Assert.DoesNotContain(harness.Bob.Received, m => m is UpdateAddHtlcMessage);
    }

    /// <summary>
    /// Pumps the harness until <paramref name="paying"/> completes: the fetch runs before the first HTLC is offered, so
    /// one pump may come before there is anything to deliver.
    /// </summary>
    private static async Task<PayOfferResult> PumpUntilDoneAsync(ThreeNodeHarness harness, Task<PayOfferResult> paying)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!paying.IsCompleted && DateTime.UtcNow < deadline)
        {
            await harness.PumpAsync();
            await Task.WhenAny(paying, Task.Delay(20, TestContext.Current.CancellationToken));
        }

        return await paying;
    }

    private static async Task<ThreeNodeHarness> CreateAsync(CarolOfferIssuer issuer, string[] payers) =>
        await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
                node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;

            foreach (var payer in h.Nodes.Where(n => payers.Contains(n.Name)))
            {
                var payerKey = Enumerable.Repeat((byte)(0x50 + payer.Name.Length), 32).ToArray();
                payer.ConfigureServices = services =>
                {
                    services.AddPaymentSendServices();
                    services.RemoveAll<IOnionMessageService>();
                    services.AddSingleton<IOnionMessageService>(issuer);
                    services.RemoveAll<IBolt12Signer>();
                    services.AddSingleton<IBolt12Signer>(new TestBolt12Signer(payerKey));
                    services.AddOfferSendServices();
                };
            }
        });

    /// <summary>
    /// Carol's side of the offer, reached "over onion messages" (a test double of <see cref="IOnionMessageService"/>
    /// on the payer): each invoice_request makes a real invoice at Carol (<c>IInvoiceService</c>, saved before the
    /// answer) with a real blinded path to her (<see cref="BlindedPathBuilder"/>, introduced by Bob), answered as a
    /// BOLT 12 invoice signed by the offer's issuer key.
    /// </summary>
    private sealed class CarolOfferIssuer : IOnionMessageService
    {
        private ThreeNodeHarness? _harness;
        private (byte[] Request, byte[] Invoice)? _last;

        public TestOfferIssuer Offers { get; } = new();
        public string? Refusal { get; init; }
        public bool ReplayLastInvoice { get; set; }
        public string? LastPayerNote { get; private set; }
        public bool IsAvailable => true;

        public void Attach(ThreeNodeHarness harness) => _harness = harness;

        public void HandleIncoming(IPeerService peer, OnionMessageMessage message) =>
            throw new NotSupportedException();

        public Task<OnionMessageSendResult> SendAsync(OnionMessageDestination destination,
                                                      OnionMessageContents contents,
                                                      Domain.Protocol.Onion.Models.BlindedPath? replyPath,
                                                      CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<OnionMessageSendResult> SendAndWaitForReplyAsync(
            OnionMessageDestination destination, OnionMessageContents contents,
            IReadOnlyCollection<ulong> expectedReplyTypes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var harness = _harness ?? throw new InvalidOperationException("Attach the harness first.");
            Assert.Equal(Offers.NodeId, destination.NodeId);
            var request = Assert.Single(contents.Records);
            Assert.Equal(OnionMessageConstants.InvoiceRequestType, request.Type);
            var stream = Bolt12Wire.ParseStream(request.Value);
            LastPayerNote = OfferToPay.ReadUtf8(stream, Domain.Offers.Constants.Bolt12TlvTypes.InvreqPayerNote);

            if (Refusal is not null)
                return Reply(OnionMessageConstants.InvoiceErrorType, TestOfferIssuer.CreateInvoiceError(Refusal));

            byte[] invoiceBytes;
            if (ReplayLastInvoice && _last is { } last)
            {
                // The same invoice_request (same metadata) gets the invoice issued before the restart; a new one would
                // not match it, so the payer's request is rebuilt from what it sent before
                invoiceBytes = Offers.CreateInvoice(request.Value, ExtractHash(last.Invoice),
                                                    await PathsAsync(harness, ExtractHash(last.Invoice)),
                                                    AmountMsat, DateTimeOffset.UtcNow);
            }
            else
            {
                var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(LightningMoney.MilliSatoshis(AmountMsat),
                                                                              "bolt12 harness", null,
                                                                              cancellationToken);
                invoiceBytes = Offers.CreateInvoice(request.Value, invoice.PaymentHash,
                                                    await PathsAsync(harness, invoice.PaymentHash), AmountMsat,
                                                    DateTimeOffset.UtcNow);
            }

            _last = (request.Value.ToArray(), invoiceBytes);
            return Reply(OnionMessageConstants.InvoiceType, invoiceBytes);
        }

        private static Domain.Crypto.ValueObjects.Hash ExtractHash(byte[] invoice)
        {
            Assert.True(Bolt12Wire.ParseStream(invoice)
                                  .TryGetValue(Domain.Offers.Constants.Bolt12TlvTypes.InvoicePaymentHash,
                                               out var hash));
            return new Domain.Crypto.ValueObjects.Hash(hash.ToArray());
        }

        private static async Task<IReadOnlyList<Domain.Protocol.Onion.Models.BlindedPaymentPath>> PathsAsync(
            ThreeNodeHarness harness, Domain.Crypto.ValueObjects.Hash paymentHash)
        {
            // Carol holds Bob's signed channel_update of their channel (the gossip exchange the harness leaves out)
            Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                               .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var update));
            harness.Carol.Services.GetRequiredService<IChannelUpdateService>()
                   .HandleRemoteChannelUpdate(harness.Bob.NodeId, update!);

            var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(paymentHash));
            var builder = harness.Carol.Services.GetRequiredService<BlindedPathBuilder>();
            return await builder.BuildAsync(new BlindedPathRequest(invoice!.Preimage, invoice.Amount,
                                                                   invoice.MinFinalCltvExpiry,
                                                                   ThreeNodeHarness.BlockHeight,
                                                                   IncludePrivateChannels: true),
                                            TestContext.Current.CancellationToken);
        }

        private static OnionMessageSendResult Reply(ulong type, byte[] value) =>
            new(OnionMessageSendStatus.Replied,
                new ReceivedOnionMessage(OnionMessageContents.Single(type, value), null, new byte[] { 1 },
                                         OfferSendTestData.Key(0x05)));
    }
}