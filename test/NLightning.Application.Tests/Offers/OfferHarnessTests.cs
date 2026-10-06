using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers;

using Application.Gossip.Interfaces;
using Application.Offers;
using Application.Offers.Receive;
using Application.Offers.Send;
using Application.OnionMessages;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// BOLT 12 plan B4-T4 (lane B12-E step 2): one nltg node pays another's offer end to end over
/// <see cref="ThreeNodeHarness"/> (Alice - Bob - Carol: production channels, switches, Sphinx, route blinding and
/// SQLite), with every BOLT 12 piece in production form. Carol issues the offer with lane B12-D's
/// <see cref="OfferService"/> and answers the invoice_request with its <see cref="InvoiceRequestHandler"/>; the payer
/// builds the request and verifies the invoice with <see cref="OfferPaymentService"/>, signs with the node's real
/// <c>Bolt12Signer</c> (lane B12-B), and pays with <c>PaymentService.PayBlindedAsync</c>. Onion messages go through the
/// production <see cref="OnionMessageService"/> of each node (Bob forwards them); only the connections between the
/// nodes are the harness's (<see cref="OnionMessageLinks"/>, the send path <c>PeerManager</c> implements).
/// </summary>
public class OfferHarnessTests
{
    private const ulong AmountMsat = 40_000_000;

    [Fact]
    public async Task Given_CarolsOffer_When_AlicePaysIt_Then_CarolSettlesItsBolt12InvoiceAndAliceStoresTheDetails()
    {
        // Arrange
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));

        // Act
        var result = await PayAsync(harness, harness.Alice, new PayOfferRequest(offer.Bolt12, PayerNote: "from alice"));

        // Assert: the offer went over its path (Bob introduces it) and the invoice came back over Alice's reply path
        Assert.Equal(FetchInvoiceStatus.Received, result.Fetch.Status);
        Assert.Contains(("Alice", "Bob"), links.Delivered);
        Assert.Contains(("Bob", "Carol"), links.Delivered);
        Assert.Contains(("Carol", "Bob"), links.Delivered);
        Assert.Contains(("Bob", "Alice"), links.Delivered);
        var payment = result.Payment!.Payment;
        Assert.True(payment.Status == PaymentStatus.Succeeded, payment.FailureReason);
        Assert.Equal(harness.Carol.NodeId, payment.PayeeNodeId);
        Assert.Equal(AmountMsat, payment.Amount.MilliSatoshi);

        // Carol's invoice is the BOLT 12 one she answered with, settled with the preimage Alice got
        var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                              .GetByPaymentHashAsync(result.Fetch.Invoice!.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, invoice!.Status);
        Assert.Equal(InvoiceKind.Bolt12, invoice.Kind);
        Assert.Equal(offer.OfferId, invoice.Bolt12!.OfferId);
        Assert.Equal("from alice", invoice.Bolt12.PayerNote);
        Assert.Equal(result.Fetch.Invoice!.InvoiceBytes.ToArray(), invoice.Bolt12.InvoiceBytes.ToArray());
        Assert.Equal(invoice.Preimage!.Value, payment.Preimage);

        // Alice's payment row keeps the offer, the invoice and her request's metadata
        var stored = await harness.Alice.InScopeAsync(u => u.PaymentDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        Assert.Equal(offer.Bolt12, stored.Bolt12!.Offer);
        Assert.Equal(invoice.Bolt12.InvoiceBytes.ToArray(), stored.Bolt12.InvoiceBytes.ToArray());
        Assert.Equal("from alice", stored.Bolt12.PayerNote);
        Assert.Equal(32, stored.Bolt12.InvoiceRequestMetadata.Length);

        // NL-602: Carol recorded what her invoice received with the offer's id and the payer's note; Alice recorded
        // the payment with the offer she paid
        var received = Assert.Single(await harness.Carol.InScopeAsync(u => u.AccountingEventDbRepository
                                                                           .GetUnsealedAsync(1_000)));
        Assert.Equal(AccountingEventKind.InvoiceSettled, received.Kind);
        Assert.Equal((long)invoice.AmountReceived!.MilliSatoshi, received.AmountMsat);
        Assert.Equal("bolt12", received.Details["kind"]);
        Assert.Equal(offer.OfferId.ToString(), received.Details["offerId"]);
        Assert.Equal("from alice", received.Details["payerNote"]);
        var paid = Assert.Single(await harness.Alice.InScopeAsync(u => u.AccountingEventDbRepository
                                                                       .GetUnsealedAsync(1_000)));
        Assert.Equal(AccountingEventKind.PaymentSucceeded, paid.Kind);
        Assert.Equal(-(long)stored.TotalAmount.MilliSatoshi, paid.AmountMsat);
        Assert.Equal((long)stored.Fee.MilliSatoshi, paid.FeeMsat);
        Assert.Equal(harness.Carol.NodeId, paid.Counterparty);
        Assert.Equal("bolt12", paid.Details["kind"]);
        Assert.Equal(offer.Bolt12, paid.Details["offer"]);
        Assert.Equal("from alice", paid.Details["payerNote"]);

        // NL-645: the payment also carries the offer's id, the one Carol recorded, so a rule on the offer matches both
        Assert.Equal(offer.OfferId.ToString(), paid.Details[ClassificationEngine.OfferIdDetail]);
    }

    [Fact]
    public async Task Given_ALabelledOfferAndALabelledPayment_When_AlicePays_Then_RowsAndEventsCarryTheirLabels()
    {
        // Arrange (NL-602 A3-T1): Carol labels her offer (createoffer --label/--tag), Alice her payment (payoffer)
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offerLabels = SourceLabels.Create("coffee shop", ["till=2", "branch=main"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat), labels: offerLabels);
        var paymentLabels = SourceLabels.Create("breakfast", ["category=food"]);

        // Act
        var result = await PayAsync(harness, harness.Alice, new PayOfferRequest(offer.Bolt12),
                                    new PayOfferOptions { Payment = new PayInvoiceOptions { Labels = paymentLabels } });

        // Assert: Carol's offer row and the BOLT 12 invoice she issued for it carry the offer's labels (SQLite)
        Assert.True(result.Payment!.Payment.Status == PaymentStatus.Succeeded, result.Payment.Payment.FailureReason);
        var storedOffer = await harness.Carol.InScopeAsync(u => u.OfferDbRepository.GetByIdAsync(offer.OfferId));
        Assert.Equal("coffee shop", storedOffer!.Label);
        Assert.Equal("branch=main\ntill=2", storedOffer.Tags);
        var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                              .GetByPaymentHashAsync(result.Fetch.Invoice!.PaymentHash));
        Assert.Equal("coffee shop", invoice!.Label);
        Assert.Equal(offerLabels.CanonicalTags, invoice.Tags);

        // Her InvoiceSettled copies them into its details
        var received = Assert.Single(await harness.Carol.InScopeAsync(u => u.AccountingEventDbRepository
                                                                           .GetUnsealedAsync(1_000)));
        Assert.Equal(AccountingEventKind.InvoiceSettled, received.Kind);
        Assert.Equal("coffee shop", received.Details[AccountingDetailKeys.Label]);
        Assert.Equal("2", received.Details["tag.till"]);
        Assert.Equal("main", received.Details["tag.branch"]);

        // Alice's payment row and her PaymentSucceeded carry hers
        var stored = await harness.Alice.InScopeAsync(u => u.PaymentDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal("breakfast", stored!.Label);
        Assert.Equal("category=food", stored.Tags);
        var paid = Assert.Single(await harness.Alice.InScopeAsync(u => u.AccountingEventDbRepository
                                                                       .GetUnsealedAsync(1_000)));
        Assert.Equal(AccountingEventKind.PaymentSucceeded, paid.Kind);
        Assert.Equal("breakfast", paid.Details[AccountingDetailKeys.Label]);
        Assert.Equal("food", paid.Details["tag.category"]);
        Assert.Equal(paymentLabels.CanonicalTags, SourceLabels.FromDetails(paid.Details).CanonicalTags);
    }

    [Fact]
    public async Task Given_BobIntroducesCarolsPaths_When_BobPaysTheOffer_Then_HeSendsStraightToCarol()
    {
        // Arrange: B12-PAY-02, the payer is the introduction node of the offer's path and of the invoice's path
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Bob"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));

        // Act
        var result = await PayAsync(harness, harness.Bob, new PayOfferRequest(offer.Bolt12));

        // Assert
        var payment = result.Payment!.Payment;
        Assert.True(payment.Status == PaymentStatus.Succeeded, payment.FailureReason);
        var add = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.Carol.Received,
                                                                    m => m is UpdateAddHtlcMessage));
        Assert.NotNull(add.BlindedPathTlv);
        Assert.DoesNotContain(harness.Alice.Received, m => m is UpdateAddHtlcMessage);
        Assert.DoesNotContain(links.Delivered, d => d.From == "Alice" || d.To == "Alice");
        var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                              .GetByPaymentHashAsync(result.Fetch.Invoice!.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, invoice!.Status);
    }

    [Fact]
    public async Task Given_AnAmountlessOfferWithAQuantity_When_AlicePaysAnAmountForTwo_Then_CarolSettlesThatAmount()
    {
        // Arrange
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, null, quantityMax: 5);
        var amount = LightningMoney.MilliSatoshis(12_345_000);

        // Act
        var result = await PayAsync(harness, harness.Alice, new PayOfferRequest(offer.Bolt12, amount, Quantity: 2));

        // Assert
        Assert.True(result.Payment!.Payment.Status == PaymentStatus.Succeeded, result.Payment.Payment.FailureReason);
        Assert.Equal(amount, result.Fetch.Invoice!.Amount);
        var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                              .GetByPaymentHashAsync(result.Fetch.Invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, invoice!.Status);
        Assert.Equal(2UL, invoice.Bolt12!.Quantity);
        Assert.Equal(amount, invoice.Amount);
        var received = Assert.Single(await harness.Carol.InScopeAsync(u => u.AccountingEventDbRepository
                                                                           .GetUnsealedAsync(1_000)));
        Assert.Equal("2", received.Details["quantity"]); // NL-602
    }

    [Fact]
    public async Task Given_CarolRestartsAfterAnInvoice_When_AlicePaysTheOffer_Then_TheOfferStillWorksAndSettles()
    {
        // Arrange: the offer and each invoice row are saved before they go out, and the offer path ids are derived
        // from Carol's node key, so a restart loses nothing
        var ct = TestContext.Current.CancellationToken;
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));
        var service = harness.Alice.Services.GetRequiredService<IOfferPaymentService>();
        var fetched = await service.FetchInvoiceAsync(new PayOfferRequest(offer.Bolt12), new PayOfferOptions(), ct);
        Assert.Equal(FetchInvoiceStatus.Received, fetched.Status);
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        ShareBobsChannelUpdateWithCarol(harness);

        // Act
        var result = await PayAsync(harness, harness.Alice, new PayOfferRequest(offer.Bolt12));

        // Assert
        Assert.True(result.Payment!.Payment.Status == PaymentStatus.Succeeded, result.Payment.Payment.FailureReason);
        Assert.NotEqual(fetched.Invoice!.PaymentHash, result.Fetch.Invoice!.PaymentHash);
        var first = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                            .GetByPaymentHashAsync(fetched.Invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open, first!.Status);
        Assert.Equal(offer.OfferId, first.Bolt12!.OfferId);
        var paid = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                           .GetByPaymentHashAsync(result.Fetch.Invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, paid!.Status);
    }

    [Fact]
    public async Task Given_AFetchedInvoiceHandedOut_When_AlicePaysItByItsString_Then_CarolSettlesIt()
    {
        // Arrange: CLN's fetchinvoice + xpay shape (captaind, NL-1151): the invoice string leaves the node in between
        var ct = TestContext.Current.CancellationToken;
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));
        var service = harness.Alice.Services.GetRequiredService<IOfferPaymentService>();
        var fetching = service.FetchInvoiceAsync(new PayOfferRequest(offer.Bolt12, PayerNote: "for the ark"),
                                                 new PayOfferOptions(), ct);
        var fetched = await PumpUntilDoneAsync(harness, fetching);
        Assert.Equal(FetchInvoiceStatus.Received, fetched.Status);
        var invoiceString = Bolt12Bech32.Encode(Bolt12Constants.InvoiceHrp, fetched.Invoice!.InvoiceBytes.Span);

        // Act
        var result = await PumpUntilDoneAsync(harness,
                                              service.PayFetchedInvoiceAsync(invoiceString, new PayInvoiceOptions(),
                                                                             ct));

        // Assert: paid over the fetch's verified paths, recorded as a BOLT 12 payment of that offer and invoice
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(fetched.Invoice.PaymentHash, result.Payment.PaymentHash);
        Assert.Equal(offer.Bolt12, result.Payment.Bolt12!.Offer);
        Assert.Equal("for the ark", result.Payment.Bolt12.PayerNote);
        Assert.True(fetched.Invoice.InvoiceBytes.Span.SequenceEqual(result.Payment.Bolt12.InvoiceBytes.Span));
        var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                              .GetByPaymentHashAsync(fetched.Invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, invoice!.Status);
    }

    [Fact]
    public async Task Given_AFetchForgottenByARestart_When_PaidByString_Then_ItIsVerifiedFromTheInvoiceAndPaid()
    {
        // Arrange: Alice fetched the invoice, then lost her memory of it (a restart between captaind's FetchInvoice and
        // its xpay): the same node's keys, an empty cache (NL-1157)
        var ct = TestContext.Current.CancellationToken;
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));
        var fetched = await PumpUntilDoneAsync(harness, harness.Alice.Services
                                                              .GetRequiredService<IOfferPaymentService>()
                                                              .FetchInvoiceAsync(new PayOfferRequest(offer.Bolt12,
                                                                                     PayerNote: "after a restart"),
                                                                                 new PayOfferOptions(), ct));
        var invoiceString = Bolt12Bech32.Encode(Bolt12Constants.InvoiceHrp, fetched.Invoice!.InvoiceBytes.Span);
        var restarted = NewOfferPaymentService(harness, harness.Alice.Services.GetRequiredService<IBolt12Signer>());

        // Act
        var result = await PumpUntilDoneAsync(harness,
                                              restarted.PayFetchedInvoiceAsync(invoiceString, new PayInvoiceOptions(),
                                                                               ct));

        // Assert: paid over the invoice's verified paths as a BOLT 12 payment of the same offer and note
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(fetched.Invoice.PaymentHash, result.Payment.PaymentHash);
        Assert.Equal("after a restart", result.Payment.Bolt12!.PayerNote);
        Assert.Equal(offer.Bolt12, result.Payment.Bolt12.Offer); // re-encoded from the mirrored offer records
        var invoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                              .GetByPaymentHashAsync(fetched.Invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, invoice!.Status);
    }

    [Fact]
    public async Task Given_InvoicesThisNodeDidNotRequest_When_PaidByString_Then_RefusedAndNothingSent()
    {
        // Arrange: an invoice answering Alice's request is not another node's to pay (its invreq_payer_id is derived
        // with Alice's key), nor is anything that is not an lni string
        var ct = TestContext.Current.CancellationToken;
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));
        var fetched = await PumpUntilDoneAsync(harness, harness.Alice.Services
                                                              .GetRequiredService<IOfferPaymentService>()
                                                              .FetchInvoiceAsync(new PayOfferRequest(offer.Bolt12),
                                                                                 new PayOfferOptions(), ct));
        var invoiceString = Bolt12Bech32.Encode(Bolt12Constants.InvoiceHrp, fetched.Invoice!.InvoiceBytes.Span);
        var stranger = NewOfferPaymentService(harness, harness.Carol.Services.GetRequiredService<IBolt12Signer>());

        // Act / Assert
        var notOurs = await Assert.ThrowsAsync<ArgumentException>(
                          () => stranger.PayFetchedInvoiceAsync(invoiceString, new PayInvoiceOptions(), ct));
        Assert.Contains("does not answer an invoice_request of this node", notOurs.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(
            () => stranger.PayFetchedInvoiceAsync(offer.Bolt12, new PayInvoiceOptions(), ct));
        Assert.DoesNotContain(harness.Bob.Received, m => m is UpdateAddHtlcMessage);
    }

    /// <summary>An offer payment service on Alice's node with an empty fetch cache and the given BOLT 12 keys.</summary>
    private static OfferPaymentService NewOfferPaymentService(ThreeNodeHarness harness, IBolt12Signer signer) =>
        new(harness.Alice.Services.GetRequiredService<IOnionMessageService>(),
            harness.Alice.Services.GetRequiredService<IPaymentService>(),
            harness.Alice.Services.GetRequiredService<IOptions<NodeOptions>>(),
            NullLogger<OfferPaymentService>.Instance, TimeProvider.System, signer);

    [Fact]
    public async Task Given_CarolDisabledTheOffer_When_AlicePays_Then_TheInvoiceErrorIsReportedAndNothingPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var links = new OnionMessageLinks();
        await using var harness = await CreateAsync(links, payers: ["Alice"]);
        var offer = await CreateOfferAsync(harness, LightningMoney.MilliSatoshis(AmountMsat));
        await harness.Carol.Services.GetRequiredService<IOfferService>().DisableOfferAsync(offer.OfferId, ct);

        // Act
        var result = await harness.Alice.Services.GetRequiredService<IOfferPaymentService>()
                                  .PayOfferAsync(new PayOfferRequest(offer.Bolt12), new PayOfferOptions(), ct);

        // Assert
        Assert.Equal(FetchInvoiceStatus.InvoiceError, result.Fetch.Status);
        Assert.Equal("Offer no longer available", result.Fetch.Error);
        Assert.Equal(1, result.Fetch.Attempts);
        Assert.Null(result.Payment);
        Assert.DoesNotContain(harness.Bob.Received, m => m is UpdateAddHtlcMessage);
    }

    /// <summary>
    /// Carol's offer through the production <see cref="IOfferService"/>: she has no announced channel, so it carries an
    /// offer path introduced by Bob, her only onion-message peer.
    /// </summary>
    private static async Task<OfferModel> CreateOfferAsync(ThreeNodeHarness harness, LightningMoney? amount,
                                                           ulong? quantityMax = null, SourceLabels? labels = null)
    {
        ShareBobsChannelUpdateWithCarol(harness);
        var offers = harness.Carol.Services.GetRequiredService<IOfferService>();
        Assert.True(offers.IsAvailable);
        // Bob has an open channel with Carol, so the reachability warning (NL-452) stays off
        var request = new CreateOfferRequest(amount, "harness offer", QuantityMax: quantityMax)
        {
            Labels = labels ?? SourceLabels.None
        };
        var offer = (await offers.CreateOfferAsync(request, TestContext.Current.CancellationToken)).Offer;
        Assert.True(offer.HasPaths);
        return offer;
    }

    /// <summary>
    /// Carol takes Bob's signed channel_update of their channel (the exchange at connection the harness leaves out; it
    /// lives in memory, so again after a restart): her invoices' blinded payment paths need Bob's policy.
    /// </summary>
    private static void ShareBobsChannelUpdateWithCarol(ThreeNodeHarness harness)
    {
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var update));
        harness.Carol.Services.GetRequiredService<IChannelUpdateService>()
               .HandleRemoteChannelUpdate(harness.Bob.NodeId, update!);
    }

    private static async Task<PayOfferResult> PayAsync(ThreeNodeHarness harness, SwitchNode payer,
                                                       PayOfferRequest request, PayOfferOptions? options = null)
    {
        var paying = payer.Services.GetRequiredService<IOfferPaymentService>()
                          .PayOfferAsync(request, options ?? new PayOfferOptions(),
                                         TestContext.Current.CancellationToken);
        var result = await PumpUntilDoneAsync(harness, paying);
        Assert.True(result.Fetch.Status == FetchInvoiceStatus.Received, result.Fetch.Error);
        Assert.NotNull(result.Payment);
        return result;
    }

    /// <summary>
    /// Pumps the harness until <paramref name="paying"/> completes: the fetch runs over onion messages before the first
    /// HTLC is offered, so one pump may come before there is anything to deliver.
    /// </summary>
    private static async Task<T> PumpUntilDoneAsync<T>(ThreeNodeHarness harness, Task<T> paying)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!paying.IsCompleted && DateTime.UtcNow < deadline)
        {
            await harness.PumpAsync();
            await Task.WhenAny(paying, Task.Delay(20, TestContext.Current.CancellationToken));
        }

        return await paying;
    }

    /// <summary>
    /// Every node gets the production onion-message service (<c>AddOnionMessageServices</c>, the production packet
    /// builder from <c>AddBitcoinInfrastructure</c>) over <paramref name="links"/>; Carol gets lane B12-D's offers
    /// (<c>AddOffersServices</c>), and each payer the payment send side and <c>AddOfferSendServices</c>. The BOLT 12
    /// signer is the node's real <c>Bolt12Signer</c> over its <c>LocalLightningSigner</c>.
    /// </summary>
    private static async Task<ThreeNodeHarness> CreateAsync(OnionMessageLinks links, string[] payers)
    {
        var harness = await ThreeNodeHarness.CreateAsync(h =>
        {
            links.Attach(h);
            foreach (var node in h.Nodes)
            {
                node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
                node.Options.Features.OptionOnionMessages = FeatureSupport.Optional;
                var isPayer = payers.Contains(node.Name);
                var isIssuer = node == h.Carol;
                node.ConfigureServices = services =>
                {
                    services.RemoveAll<IPeerManager>();
                    services.AddSingleton(links.PeerManagerFor(node));
                    services.RemoveAll<IPeerOnionMessageOutbox>();
                    services.AddSingleton<IPeerOnionMessageOutbox>(links.OutboxFor(node));
                    services.AddOnionMessageServices();
                    if (isIssuer)
                        services.AddOffersServices();
                    if (isPayer)
                    {
                        services.AddPaymentSendServices();
                        services.AddOfferSendServices();
                    }
                };
            }
        });

        return harness;
    }

    /// <summary>
    /// The connections between the harness nodes for onion messages, standing in for <c>PeerManager</c>: each node is
    /// connected to the nodes it has a channel with (Alice - Bob - Carol), every connection negotiated
    /// <c>option_onion_messages</c>, and a queued message is handed straight to the running receiver's
    /// <see cref="IOnionMessageService.HandleIncoming"/> (which only queues it), as the peer's read loop would. A
    /// stopped node is not connected.
    /// </summary>
    private sealed class OnionMessageLinks
    {
        private ThreeNodeHarness? _harness;

        /// <summary>Every onion message handed over, as (sender, receiver) names.</summary>
        public ConcurrentQueue<(string From, string To)> Delivered { get; } = new();

        public void Attach(ThreeNodeHarness harness) => _harness = harness;

        public IPeerManager PeerManagerFor(SwitchNode node)
        {
            var peerManager = new Mock<IPeerManager>();
            peerManager.Setup(m => m.ListPeers())
                       .Returns(() => Peers(node).Select(p => ToPeerModel(p)).ToList());
            peerManager.Setup(m => m.GetPeer(It.IsAny<CompactPubKey>()))
                       .Returns((CompactPubKey id) => Peers(node).Where(p => p.NodeId == id)
                                                                  .Select(p => ToPeerModel(p))
                                                                  .FirstOrDefault());
            return peerManager.Object;
        }

        public IPeerOnionMessageOutbox OutboxFor(SwitchNode node) => new Outbox(this, node);

        private IEnumerable<SwitchNode> Peers(SwitchNode node)
        {
            var harness = _harness ?? throw new InvalidOperationException("Attach the harness first.");
            if (!node.IsRunning)
                return [];

            // Bob lists Carol first: a reply path is introduced by the first listed peer (M6 ReplyPathFactory), and
            // Carol has no graph to reach Alice, so a reply path of Bob's through Alice would be NoPath for her (the
            // reply path ignores which peer the request leaves through; reported to the ledger)
            IEnumerable<SwitchNode> linked = node == harness.Bob
                                                 ? [harness.Carol, harness.Alice]
                                                 : [harness.Bob];
            return linked.Where(p => p.IsRunning && node.IsPeerAlive(p.NodeId) && p.IsPeerAlive(node.NodeId));
        }

        private static PeerModel ToPeerModel(SwitchNode peer)
        {
            var model = new PeerModel(peer.NodeId, "127.0.0.1", 9735, "harness");
            model.SetPeerService(new HarnessOnionMessagePeer(peer.NodeId));
            return model;
        }

        private sealed class Outbox(OnionMessageLinks links, SwitchNode node) : IPeerOnionMessageOutbox
        {
            public bool CanSendOnionMessage(CompactPubKey peerNodeId) =>
                links.Peers(node).Any(p => p.NodeId == peerNodeId);

            public bool TryEnqueueOnionMessage(CompactPubKey peerNodeId, OnionMessageMessage message)
            {
                var target = links.Peers(node).FirstOrDefault(p => p.NodeId == peerNodeId);
                if (target is null)
                    return false;

                try
                {
                    target.Services.GetRequiredService<IOnionMessageService>()
                          .HandleIncoming(new HarnessOnionMessagePeer(node.NodeId), message);
                }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
                {
                    // The receiver stopped meanwhile: the message is lost, as on a dropped connection
                    return false;
                }

                links.Delivered.Enqueue((node.Name, target.Name));
                return true;
            }
        }
    }

    /// <summary>
    /// The receiver's view of the connection a harness onion message came over: only its node id and the negotiated
    /// <c>option_onion_messages</c> are read.
    /// </summary>
    private sealed class HarnessOnionMessagePeer(CompactPubKey peerPubKey) : IPeerService
    {
        public CompactPubKey PeerPubKey { get; } = peerPubKey;

        public FeatureOptions Features { get; } = new()
        {
            OptionOnionMessages = FeatureSupport.Optional,
            OptionRouteBlinding = FeatureSupport.Optional
        };

        /// <summary>The same options as <see cref="Features"/>: the fake does not model the negotiation separately.</summary>
        public FeatureOptions PeerFeatures => Features;

        public DateTimeOffset? LastMessageReceivedAt => null;
        public AddressDescriptor? ObservedAddress => null;
        public WillFundRates? LiquidityRates => null;

#pragma warning disable CS0067 // events of the interface the harness never raises
        public event EventHandler<PeerDisconnectedEventArgs>? OnDisconnect;
        public event EventHandler<ChannelMessageEventArgs>? OnChannelMessageReceived;
        public event EventHandler<AttentionMessageEventArgs>? OnAttentionMessageReceived;
        public event EventHandler<Exception>? OnExceptionRaised;
        public event EventHandler<ChannelUpdateMessage>? OnChannelUpdateReceived;
#pragma warning restore CS0067

        public Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task WaitForInitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Disconnect(Exception? exception = null)
        {
        }

        public Task SendMessageAsync(IChannelMessage replyMessage) => throw new NotSupportedException();
        public Task SendGossipMessageAsync(IMessage message) => throw new NotSupportedException();
        public Task SendPeerStorageMessageAsync(IMessage message) => throw new NotSupportedException();
        public Task SendWarningAsync(WarningException we) => throw new NotSupportedException();
        public Task SendErrorAsync(ErrorMessage errorMessage) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}