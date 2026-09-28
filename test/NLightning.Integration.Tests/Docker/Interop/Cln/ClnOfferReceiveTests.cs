using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Application.Gossip.Interfaces;
using Daemon.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers;
using Domain.Offers.Enums;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Models;
using Fixtures;
using Utils;

/// <summary>
/// Proof B12, receive side (BOLT 12 plan §5 "Proof B12", lane B12-D): Core Lightning <see cref="ClnFixture.ClnTag"/>
/// fetches and pays offers our node created. Our node funds a private channel to CLN (pushing half of it, so CLN can
/// pay us) and has no public channel, so every offer of ours carries <c>offer_paths</c> introduced by CLN (B12-OFR-02,
/// case (c)) and every invoice blinded payment paths whose introduction node is CLN itself.
/// </summary>
/// <remarks>
/// <para>(a) an offer with an amount: CLN's <c>fetchinvoice</c> gets our invoice, <c>decode</c> shows our node id,
/// the amount and our paths, CLN's <c>xpay</c> pays it and our invoice is Settled for that amount plus what our own
/// dummy hop kept (NL-526, bounded by the path's pay info); (b) an
/// amountless offer fetched with <c>amount_msat</c> and paid; (d) CLN's <c>xpay</c> of the offer string itself; (e)
/// after <c>disableoffer</c> CLN's <c>fetchinvoice</c> fails with our <c>invoice_error</c> (CLN code 1004, "Remote node
/// sent failure message", as in Proof M6 (c)).</para>
/// <para>Written in lane B12-D against the B12-0 contracts: it needs the lanes' signer (B12-B, registered by
/// <c>AddBitcoinInfrastructure</c>) and persistence (B12-C, <c>IUnitOfWork.OfferDbRepository</c>). The offer
/// services and commands come from the node composition (<c>AddApplicationServices</c> and
/// <c>AddNltgNodeServices</c>) since the B12 integration.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnOfferReceiveTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;
    private const string CacheKey = "cln-offer-receive-channel";

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(500_000);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;

    public ClnOfferReceiveTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private ClnClient Cln => _fixture.Cln;

    private ClnChannelSession Session => _session ?? throw new InvalidOperationException("No session.");

    private NLightningTestNode Node => Session.Node;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var info = await Cln.GetInfoAsync(ct);
        Console.WriteLine($"[cln] version {info["version"]}");
        _session = await ClnChannelSession.GetOrBuildDetachedAsync(
                       factory => _fixture.GetOrCreateAsync(CacheKey, factory), BuildAsync,
                       ClnChannelSession.BuildTimeout, "CLN offer channel", ct);
        await _session.PrepareAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[cln] CLN offers log lines:\n"
                        + await Cln.GetLogLinesAsync("offers", CancellationToken.None, 60));
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60, "unusual"));
        if (DockerDiagnostics.CurrentTestFailed && _session is not null)
            Console.WriteLine($"[cln] channel: {await _session.DescribeAsync(CancellationToken.None)}");
    }

    private Task<ClnChannelSession> BuildAsync(CancellationToken cancellationToken) =>
        ClnChannelSession.BuildOurFundedAsync(_fixture, "nltg-offers", s_capacity, s_push, cancellationToken);

    /// <summary>
    /// Proof B12 receive (a) and (c): an offer of 10,000 sat; CLN fetches an invoice through our offer path (CLN is its
    /// introduction node), <c>decode</c> shows our node id, the amount and our blinded paths, and CLN pays it through
    /// them: our invoice row is Settled for 10,000 sat plus our dummy hop's fee (NL-526).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurOfferWithAnAmount_When_ClnFetchesAndPays_Then_OurInvoiceIsSettled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await CreateOfferAsync(new CreateOfferClientRequest
        {
            Amount = LightningMoney.Satoshis(10_000),
            Description = "nltg-b12-a"
        }, ct);
        Assert.True(offer.HasPaths, "a node with only private channels must put offer_paths in its offers");
        var decodedOffer = await Cln.CallAsync("decode", ct, ("string", offer.Bolt12));
        Console.WriteLine($"[cln] decode offer: {decodedOffer.ToJsonString()}");
        Assert.True(decodedOffer["valid"]!.GetValue<bool>());
        Assert.Equal(Node.NodeIdHex, decodedOffer["offer_issuer_id"]!.GetValue<string>());

        var balanceBefore = await GetOurBalanceAsync(ct);

        // Act
        var fetched = await Cln.CallAsync("fetchinvoice", ct, ("offer", offer.Bolt12), ("timeout", 60));
        var invoice = fetched["invoice"]!.GetValue<string>();
        var decoded = await Cln.CallAsync("decode", ct, ("string", invoice));
        Console.WriteLine($"[cln] decode invoice: {decoded.ToJsonString()}");
        var paid = await PayAsync(invoice, null, ct);

        // Assert
        Assert.True(decoded["valid"]!.GetValue<bool>());
        Assert.Equal(Node.NodeIdHex, decoded["invoice_node_id"]!.GetValue<string>());
        Assert.Equal(10_000_000L, decoded["invoice_amount_msat"]!.GetValue<long>());
        var paths = decoded["invoice_paths"]!.AsArray();
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.Equal(_fixture.ClnNodeId, p!["first_node_id"]!.GetValue<string>()));
        await AssertSettledAsync(decoded, paid, 10_000_000UL, balanceBefore, ct);
    }

    /// <summary>
    /// Proof B12 receive (b): an amountless offer, fetched with <c>amount_msat</c> and paid for exactly that amount.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnAmountlessOffer_When_ClnFetchesWithAnAmountAndPays_Then_SettledForThatAmount()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await CreateOfferAsync(new CreateOfferClientRequest { Description = "nltg-b12-b" }, ct);
        var balanceBefore = await GetOurBalanceAsync(ct);

        // Act
        var fetched = await Cln.CallAsync("fetchinvoice", ct, ("offer", offer.Bolt12), ("amount_msat", 7_777_000),
                                          ("timeout", 60));
        var invoice = fetched["invoice"]!.GetValue<string>();
        var decoded = await Cln.CallAsync("decode", ct, ("string", invoice));
        var paid = await PayAsync(invoice, null, ct);

        // Assert
        Assert.Equal(7_777_000L, decoded["invoice_amount_msat"]!.GetValue<long>());
        await AssertSettledAsync(decoded, paid, 7_777_000UL, balanceBefore, ct);
    }

    /// <summary>
    /// Proof B12 receive (d): CLN's <c>xpay</c> of the offer string (it fetches the invoice itself, CLN 25.09+).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurOffer_When_ClnXpaysTheOfferString_Then_OurInvoiceIsSettled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await CreateOfferAsync(new CreateOfferClientRequest
        {
            Amount = LightningMoney.Satoshis(3_000),
            Description = "nltg-b12-d"
        }, ct);
        var before = await ListOfferAsync(offer.OfferId, ct);
        var balanceBefore = await GetOurBalanceAsync(ct);

        // Act
        var paid = await Cln.CallAsync("xpay", ct, ("invstring", offer.Bolt12));
        Console.WriteLine($"[cln] xpay offer: {paid.ToJsonString()}");

        // Assert
        var preimage = Convert.FromHexString(paid["payment_preimage"]!.GetValue<string>());
        var hash = new Hash(System.Security.Cryptography.SHA256.HashData(preimage));
        var ours = await Poll.ForAsync(async () => await Node.GetInvoiceAsync(hash, ct) is { Status: InvoiceStatus.Settled } i
                                                       ? i
                                                       : null,
                                       TimeSpan.FromSeconds(30), "our invoice settled", ct);
        await AssertReceivedWithOurDummyHopsAsync(ours, 3_000_000UL, ct);
        var after = await ListOfferAsync(offer.OfferId, ct);
        Assert.Equal(before.PaidInvoices + 1, after.PaidInvoices);
        await AssertBalanceGrewByAsync(balanceBefore, ours.AmountReceived!.MilliSatoshi, ct);
    }

    /// <summary>
    /// Proof B12 receive (e): after <c>disableoffer</c>, CLN's <c>fetchinvoice</c> gets our <c>invoice_error</c>
    /// (CLN 1004) instead of an invoice, and no invoice row is added.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ADisabledOffer_When_ClnFetches_Then_ItGetsOurInvoiceError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await CreateOfferAsync(new CreateOfferClientRequest
        {
            Amount = LightningMoney.Satoshis(1_000),
            Description = "nltg-b12-e"
        }, ct);
        var disabled = await HandleAsync<DisableOfferClientRequest, DisableOfferClientResponse>(
                           new DisableOfferClientRequest { OfferId = offer.OfferId }, ct);
        Assert.True(disabled.Changed);
        Assert.Equal(OfferStatus.Disabled, disabled.Offer.Status);

        // Act
        var error = await Assert.ThrowsAsync<ClnRpcException>(
                        () => Cln.CallAsync("fetchinvoice", ct, ("offer", offer.Bolt12), ("timeout", 30)));

        // Assert
        Console.WriteLine($"[cln] fetchinvoice of a disabled offer: {error.Code} {error.ClnMessage}");
        Assert.Equal(1004, error.Code);
        var after = await ListOfferAsync(offer.OfferId, ct);
        Assert.Equal(0, after.UnpaidInvoices);
        Assert.Equal(0, after.PaidInvoices);
    }

    private async Task<JsonNode> PayAsync(string invoice, long? amountMsat, CancellationToken ct)
    {
        (string, object)[] args = amountMsat is { } amount
                                      ? [("invstring", invoice), ("amount_msat", amount)]
                                      : [("invstring", invoice)];
        var paid = await Cln.CallAsync("xpay", ct, args);
        Console.WriteLine($"[cln] xpay: {paid.ToJsonString()}");
        return paid;
    }

    private async Task AssertSettledAsync(JsonNode decoded, JsonNode paid, ulong amountMsat,
                                          LightningMoney balanceBefore, CancellationToken ct)
    {
        var hash = new Hash(Convert.FromHexString(decoded["invoice_payment_hash"]!.GetValue<string>()));
        var preimage = Convert.FromHexString(paid["payment_preimage"]!.GetValue<string>());
        Assert.Equal((byte[])hash, System.Security.Cryptography.SHA256.HashData(preimage));
        var ours = await Poll.ForAsync(async () => await Node.GetInvoiceAsync(hash, ct) is { Status: InvoiceStatus.Settled } i
                                                       ? i
                                                       : null,
                                       TimeSpan.FromSeconds(30), "our invoice settled", ct);
        await AssertReceivedWithOurDummyHopsAsync(ours, amountMsat, ct);
        Assert.Equal((long)amountMsat, paid["amount_msat"]!.GetValue<long>());
        await AssertBalanceGrewByAsync(balanceBefore, ours.AmountReceived!.MilliSatoshi, ct);
    }

    /// <summary>
    /// NL-526: our invoice paths end with dummy hops of our own node (NL-440, <c>Node:Invoices:BlindedPathDummyHops</c>),
    /// whose fee a payer pays like any other hop's and which our final hop keeps (BOLT 4: a final node may accept more
    /// than the amount). The invoice records what the HTLC carried: at least the amount, and at most what a
    /// spec-following introduction node forwards to us when the payer pays exactly the path's <c>blinded_payinfo</c>
    /// fee (BOLT 4 "Route Blinding": <c>amt_to_forward = ((amount_msat - fee_base_msat) * 1000000 + 1000000 +
    /// fee_proportional_millionths - 1) / (1000000 + fee_proportional_millionths)</c> with CLN's policy towards us),
    /// i.e. the amount plus our dummy hops' aggregated fee. Each path's pay info is checked to aggregate CLN's policy
    /// once per relaying hop (CLN and each dummy hop), so the bound is computed from the path we built.
    /// </summary>
    private async Task AssertReceivedWithOurDummyHopsAsync(InvoiceInfoClientResponse ours, ulong amountMsat,
                                                           CancellationToken ct)
    {
        // listinvoices shows what the HTLC carried; the stored invoice holds the paths we built
        var received = ours.AmountReceived!.MilliSatoshi;
        InvoiceModel? stored;
        using (var scope = Node.Services.CreateScope())
            stored = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().InvoiceDbRepository
                                .GetByPaymentHashAsync(ours.PaymentHash);
        Assert.Equal(received, stored!.AmountReceived!.MilliSatoshi);
        var invoice = Bolt12Invoice.Parse(stored.Bolt12!.InvoiceBytes);
        var paths = invoice.Fields.Paths!;
        var payInfos = invoice.Fields.BlindedPay!;
        Assert.Equal(paths.Count, payInfos.Count);

        var channelId = (await Session.GetOurChannelAsync(ct)).ChannelId;
        Assert.True(Node.Services.GetRequiredService<IChannelUpdateService>()
                        .TryGetRemoteChannelUpdate(channelId, out var clnUpdate));
        var clnRelay = new BlindedPaymentRelay(clnUpdate!.CltvExpiryDelta,
                                               clnUpdate.FeeProportionalMillionths,
                                               clnUpdate.FeeBaseMsat);

        ulong maxReceived = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            var relayingHops = paths[i].Hops.Count - 1;
            Assert.True(relayingHops >= 1, "a path has CLN and our final hop at least");
            var (feeBase, feeProportional, _) = BlindedPayInfo.Aggregate(Enumerable.Repeat(clnRelay, relayingHops)
                                                                                    .ToList(), 0);
            Assert.Equal(feeBase, payInfos[i].FeeBaseMsat);
            Assert.Equal(feeProportional, payInfos[i].FeeProportionalMillionths);

            var intoPath = (UInt128)(amountMsat + payInfos[i].ComputeFeeMsat(amountMsat));
            var toUs = ((intoPath - clnRelay.FeeBaseMsat) * 1_000_000 + 1_000_000 + clnRelay.FeeProportionalMillionths
                      - 1) / (1_000_000 + clnRelay.FeeProportionalMillionths);
            maxReceived = Math.Max(maxReceived, (ulong)toUs);
            Console.WriteLine($"[nltg] path {i}: {relayingHops - 1} dummy hop(s), pay info {feeBase} msat + "
                            + $"{feeProportional} ppm, at most {toUs - amountMsat} msat for our dummy hops");
        }

        Console.WriteLine($"[nltg] received {received} msat for a {amountMsat} msat invoice");
        Assert.InRange(received, amountMsat, maxReceived);
    }

    private async Task<LightningMoney> GetOurBalanceAsync(CancellationToken ct) =>
        (await Session.GetOurChannelAsync(ct)).LocalBalance;

    /// <summary>
    /// Our side of the channel grew by exactly what our invoice records as received once the fulfill is irrevocably
    /// committed (CLN is both the payer and the introduction node of our blinded paths: what lands on our side is the
    /// amount plus our own dummy hops' fee, NL-526).
    /// </summary>
    private async Task AssertBalanceGrewByAsync(LightningMoney balanceBefore, ulong amountMsat, CancellationToken ct)
    {
        var expected = balanceBefore.MilliSatoshi + amountMsat;
        var last = balanceBefore.MilliSatoshi;
        try
        {
            await Poll.UntilAsync(async () =>
            {
                last = (await GetOurBalanceAsync(ct)).MilliSatoshi;
                return last == expected;
            }, TimeSpan.FromSeconds(30), "our channel balance grown by the amount received", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}: expected {expected} msat, last {last} msat", e);
        }
    }

    private async Task<OfferInfoClientResponse> CreateOfferAsync(CreateOfferClientRequest request,
                                                                 CancellationToken ct)
    {
        var response = await HandleAsync<CreateOfferClientRequest, CreateOfferClientResponse>(request, ct);
        Console.WriteLine($"[nltg] offer {response.Offer.OfferId}: {response.Offer.Bolt12}");
        return response.Offer;
    }

    private async Task<OfferInfoClientResponse> ListOfferAsync(Hash offerId, CancellationToken ct)
    {
        var response = await HandleAsync<ListOffersClientRequest, ListOffersClientResponse>(
                           new ListOffersClientRequest { Take = 1_000 }, ct);
        return response.Offers.Single(o => o.OfferId == offerId);
    }

    private async Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request, CancellationToken ct)
    {
        using var scope = Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }
}