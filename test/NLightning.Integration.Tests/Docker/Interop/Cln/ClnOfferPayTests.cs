using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Daemon.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Offers.Encoding;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Payments.Enums;
using Fixtures;
using Utils;

/// <summary>
/// Proof B12 pay (BOLT 12 plan §5, lane B12-E): we pay offers Core Lightning (v26.06.8) creates. Over a private
/// channel we fund (CLN's only peer), <c>payoffer</c> sends CLN an invoice_request over onion messages, verifies CLN's
/// invoice and pays it over CLN's blinded paths; CLN lists the invoice paid.
/// </summary>
/// <remarks>
/// The payer signs with the node's production <c>Bolt12Signer</c> (lane B12-B, <c>AddBitcoinInfrastructure</c>: payer
/// keys derived from the node key and the request's metadata), and the payer services come from the node composition
/// (<c>AddApplicationServices</c> and <c>AddNltgNodeServices</c>) since the B12 integration. Each test prints CLN's invoice as a <c>VECTOR cln invoice</c> line
/// for lane B12-A's captured vectors (B0-T4) and the introduction node of CLN's paths (BOLT 12 plan B12-PAY-02: with us
/// as CLN's only peer, CLN's invoice paths are expected to start at us).
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnOfferPayTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 6 * 60 * 1_000;

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;

    public ClnOfferPayTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[cln] CLN offers log lines:\n"
                        + await _fixture.Cln.GetLogLinesAsync("offers", CancellationToken.None, 60));
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60, "unusual"));
        if (_session is not null)
        {
            if (DockerDiagnostics.CurrentTestFailed)
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// (a) CLN <c>offer 10000sat</c>; we <c>payoffer</c>; CLN lists the invoice paid and our payment succeeded with
    /// CLN's <c>invoice_node_id</c>. (e) the payer note reaches CLN.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AClnOfferWithAnAmount_When_WePayIt_Then_ClnListsItPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await BuildAsync("nltg-offer-a", ct);
        var offer = await CreateOfferAsync(ct, ("amount", "10000sat"), ("description", "nltg b12 proof a"));
        var bolt12 = offer["bolt12"]!.GetValue<string>();
        await LogDecodedAsync(bolt12, ct);

        // Act
        var response = await PayAsync(new PayOfferClientRequest(bolt12) { PayerNote = "nltg-proof-note" }, ct);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, response.Fetch.Status);
        Assert.NotNull(response.Payment);
        Assert.True(response.Payment!.Status == PaymentStatus.Succeeded, response.Payment.FailureReason);
        Assert.Equal(10_000_000UL, response.Payment.Amount.MilliSatoshi);
        Assert.Equal(response.Fetch.NodeId, response.Payment.PayeeNodeId);
        var invoice = await WaitPaidAsync(offer["offer_id"]!.GetValue<string>(), ct);
        Assert.Contains("nltg-proof-note", invoice.ToJsonString());
    }

    /// <summary>
    /// (b) An amountless CLN offer paid with an amount of our choice.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnAmountlessClnOffer_When_WePayItWithAnAmount_Then_ClnListsItPaidForThatAmount()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await BuildAsync("nltg-offer-b", ct);
        var offer = await CreateOfferAsync(ct, ("amount", "any"), ("description", "nltg donation"));
        var bolt12 = offer["bolt12"]!.GetValue<string>();

        // Act
        var response = await PayAsync(new PayOfferClientRequest(bolt12)
        {
            Amount = LightningMoney.MilliSatoshis(7_654_000)
        }, ct);

        // Assert
        Assert.True(response.Payment?.Status == PaymentStatus.Succeeded,
                    response.Payment?.FailureReason ?? response.Fetch.Error);
        var invoice = await WaitPaidAsync(offer["offer_id"]!.GetValue<string>(), ct);
        Assert.Equal(7_654_000UL, invoice["amount_received_msat"]!.GetValue<ulong>());
    }

    /// <summary>
    /// (d) A quantity offer: two items at 1,000 sat.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AClnQuantityOffer_When_WePayTwoItems_Then_ClnListsItPaidForTwice()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await BuildAsync("nltg-offer-d", ct);
        var offer = await CreateOfferAsync(ct, ("amount", "1000sat"), ("description", "nltg items"),
                                           ("quantity_max", 5));
        var bolt12 = offer["bolt12"]!.GetValue<string>();

        // Act
        var response = await PayAsync(new PayOfferClientRequest(bolt12) { Quantity = 2 }, ct);

        // Assert
        Assert.True(response.Payment?.Status == PaymentStatus.Succeeded,
                    response.Payment?.FailureReason ?? response.Fetch.Error);
        Assert.Equal(2_000_000UL, response.Payment!.Amount.MilliSatoshi);
        var invoice = await WaitPaidAsync(offer["offer_id"]!.GetValue<string>(), ct);
        Assert.Equal(2_000_000UL, invoice["amount_received_msat"]!.GetValue<ulong>());
    }

    /// <summary>
    /// <c>fetchinvoice</c>: CLN's invoice verified and returned without a payment; CLN lists it unpaid.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AClnOffer_When_WeFetchAnInvoice_Then_ItIsVerifiedAndNothingIsPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await BuildAsync("nltg-offer-f", ct);
        var offer = await CreateOfferAsync(ct, ("amount", "3000sat"), ("description", "nltg fetch"));

        // Act
        using var scope = _session!.Node.Services.CreateScope();
        var fetched = await scope.ServiceProvider
                                 .GetRequiredService<IClientCommandHandler<PayOfferClientRequest,
                                     FetchInvoiceClientResponse>>()
                                 .HandleAsync(new PayOfferClientRequest(offer["bolt12"]!.GetValue<string>()), ct);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, fetched.Status);
        Assert.Equal(3_000_000UL, fetched.Amount!.MilliSatoshi);
        Assert.True(fetched.PathCount > 0);
        Console.WriteLine($"VECTOR cln invoice {Convert.ToHexString(fetched.Invoice!).ToLowerInvariant()}");
        var invoices = await _fixture.Cln.CallAsync("listinvoices", ct,
                                                    ("offer_id", offer["offer_id"]!.GetValue<string>()));
        Assert.All(invoices["invoices"]!.AsArray(), i => Assert.NotEqual("paid", i!["status"]!.GetValue<string>()));
    }

    private async Task BuildAsync(string name, CancellationToken ct)
    {
        _session = await ClnChannelSession.BuildOurFundedAsync(
                       _fixture, name, LightningMoney.Satoshis(500_000), LightningMoney.Zero, ct);
        var offers = _session.Node.Services.GetRequiredService<IOfferPaymentService>();
        Assert.IsType<Infrastructure.Bitcoin.Offers.Bolt12Signer>(
            _session.Node.Services.GetRequiredService<IBolt12Signer>());
        await Poll.UntilAsync(() => Task.FromResult(offers.IsAvailable), TimeSpan.FromSeconds(30),
                              "offer payments available (onion messages on)", ct);
    }

    /// <summary>
    /// CLN's <c>offer</c> with one offer path that CLN itself introduces (<c>dev_paths</c>; the fixture's CLN runs with
    /// <c>--developer</c>). Left to itself CLN fronts an offer with the peer of its largest enabled incoming public
    /// channel (plugins/offers.c <c>find_best_peer</c>) as soon as it has one: in a full CLN run the earlier classes
    /// leave public channels to nodes they already disposed of, so the offer's only path started at a node nobody can
    /// reach and our <c>invoice_request</c> never arrived ("No reply from offer path 0", NL-522). In a class run alone
    /// CLN has no public channel and issues a pathless offer. The path through CLN is the same in both.
    /// </summary>
    private async Task<JsonNode> CreateOfferAsync(CancellationToken ct, params (string Key, object Value)[] parameters)
    {
        await LogDefaultFrontAsync(ct);
        return await _fixture.Cln.CallAsync("offer", ct,
                                            [
                                                .. parameters,
                                                ("dev_paths", new JsonArray(new JsonArray(_fixture.ClnNodeId)))
                                            ]);
    }

    /// <summary>
    /// For the record (NL-522): the introduction node CLN would pick for an offer by itself, and whether CLN is
    /// connected to it.
    /// </summary>
    private async Task LogDefaultFrontAsync(CancellationToken ct)
    {
        try
        {
            var probe = await _fixture.Cln.CallAsync("offer", ct, ("amount", "1sat"),
                                                     ("description", $"nltg front probe {Guid.NewGuid():N}"));
            var decoded = await _fixture.Cln.CallAsync("decode", ct, ("string", probe["bolt12"]!.GetValue<string>()));
            await _fixture.Cln.CallAsync("disableoffer", ct, ("offer_id", probe["offer_id"]!.GetValue<string>()));
            var front = decoded["offer_paths"]?.AsArray().FirstOrDefault()?["first_node_id"]?.GetValue<string>();
            if (front is null)
            {
                Console.WriteLine("[cln] CLN's own offer would have no path");
                return;
            }

            var peers = (await _fixture.Cln.CallAsync("listpeers", ct))["peers"]!.AsArray();
            var connected = peers.Any(p => p?["id"]?.GetValue<string>() == front
                                        && p["connected"]?.GetValue<bool>() == true);
            Console.WriteLine($"[cln] CLN's own offer would be introduced by {front} (connected to CLN: {connected}; "
                            + $"our node {_session?.Node.NodeIdHex})");
        }
        catch (ClnRpcException e)
        {
            Console.WriteLine($"[cln] front probe failed: {e.Message}");
        }
    }

    private async Task<PayOfferClientResponse> PayAsync(PayOfferClientRequest request, CancellationToken ct)
    {
        using var scope = _session!.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<PayOfferClientRequest, PayOfferClientResponse>>();
        var response = await handler.HandleAsync(request, ct);
        Console.WriteLine($"[cln] payoffer: fetch {response.Fetch.Status} after {response.Fetch.Attempts} request(s)"
                        + $" ({response.Fetch.Error}), invoice node {response.Fetch.NodeId}, "
                        + $"{response.Fetch.PathCount} path(s); payment {response.Payment?.Status} "
                        + $"({response.Payment?.FailureReason}), fee {response.Payment?.Fee.MilliSatoshi} msat");
        if (response.Fetch.Invoice is { } invoice)
        {
            Console.WriteLine($"VECTOR cln invoice {Convert.ToHexString(invoice).ToLowerInvariant()}");
            await LogDecodedAsync(Bolt12Bech32.Encode("lni", invoice), ct);
        }

        return response;
    }

    private async Task<JsonNode> WaitPaidAsync(string offerId, CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            var invoices = await _fixture.Cln.CallAsync("listinvoices", ct, ("offer_id", offerId));
            return invoices["invoices"]!.AsArray()
                                        .FirstOrDefault(i => i!["status"]!.GetValue<string>() == "paid");
        }, TimeSpan.FromSeconds(60), "CLN lists the offer's invoice paid", ct);

    /// <summary>
    /// CLN's <c>decode</c> of an offer or invoice, for the record (paths and their introduction nodes).
    /// </summary>
    private async Task LogDecodedAsync(string bolt12, CancellationToken ct)
    {
        try
        {
            var decoded = await _fixture.Cln.CallAsync("decode", ct, ("string", bolt12));
            Console.WriteLine($"[cln] decode {bolt12[..3]}: {decoded.ToJsonString()}");
            if (_session is not null)
                Console.WriteLine($"[cln] our node id: {_session.Node.NodeIdHex}");
        }
        catch (ClnRpcException e)
        {
            Console.WriteLine($"[cln] decode of {bolt12[..3]} failed: {e.Message}");
        }
    }
}