using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Abcd;
using Daemon.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 12 offers, onion messages, route blinding and keysend against ldk-server (NL-556), over the shared private
/// channel we fund (<see cref="LdkChannelSession.GetAsync"/>): (a) we pay LDK's offer with an amount (our
/// <c>invoice_request</c> over onion messages to LDK, LDK's invoice paid over its blinded paths) and the payer note
/// reaches LDK; (b) we pay LDK's amountless offer with an amount of our choice; (c) LDK pays our offer (LDK's
/// <c>invoice_request</c> through our offer path, our invoice paid over our blinded paths, which LDK introduces); (d) LDK
/// pays our amountless offer; (e) keysend both ways with a custom record each way.
/// </summary>
/// <remarks>
/// <para>Our node has only private channels, so our offers carry <c>offer_paths</c> introduced by LDK and our invoices
/// blinded payment paths introduced by LDK too, ending with our own dummy hops (NL-440, NL-526): what we receive is the
/// amount plus our dummy hops' fee, which LDK pays as part of the paths' pay info. LDK Node's offers with only an
/// unannounced channel are introduced by LDK itself. LDK adds a random shadow CLTV offset (up to 432 blocks, a random
/// walk over the deltas of its graph) to a blinded path's final CLTV: once LDK knows public channels (after
/// <see cref="LdkGossipTests"/> in a full run) it exceeded our paths' old <c>max_cltv_expiry</c> margin and (c)/(d)
/// failed with <c>invalid_onion_blinding</c> (NL-723, fixed: <c>Offers:PathLifetimeMarginBlocks</c> 1008). Our payments
/// over LDK's blinded paths log that LDK's fulfill <c>attribution_data</c> does not verify for hop 0 (NL-724). Run with <c>scripts/run-interop.sh ldk Release -class
/// NLightning.Integration.Tests.Docker.Interop.Ldk.LdkOfferTests</c>.</para>
/// </remarks>
[Collection(LdkInteropCollection.Name)]
[Trait("Category", LdkInteropCollection.Category)]
public sealed class LdkOfferTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;
    private const int PayTimeoutSeconds = 90;

    /// <summary>An odd custom record type (LDK accepts odd custom TLVs of 65536 or more on a keysend).</summary>
    private const ulong CustomRecordType = 7629169;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);

    private readonly LdkFixture _fixture;
    private LdkChannelSession? _session;

    public LdkOfferTests(LdkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private LdkChannelSession Session => _session ?? throw new InvalidOperationException("No session.");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _session = await LdkChannelSession.GetAsync(_fixture, ct);
        await _session.PrepareAsync(ct);
        var offers = _session.Node.Services.GetRequiredService<IOfferPaymentService>();
        await Poll.UntilAsync(() => Task.FromResult(offers.IsAvailable), TimeSpan.FromSeconds(30),
                              "offer payments available (onion messages on)", ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed && _session is not null)
        {
            Console.WriteLine($"[ldk] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            foreach (var line in _session.Node.NodeLog.TakeLast(200))
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync([LdkFixture.LdkContainerName], 400);
        }
    }

    /// <summary>
    /// (a) LDK's offer of 10,000 sat: our <c>payoffer</c> with a payer note gets LDK's invoice (signed by LDK's node id or
    /// a key of LDK's blinded path) and pays it; LDK lists the inbound BOLT 12 payment as succeeded with our note.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnLdkOfferWithAnAmount_When_WePayIt_Then_LdkListsItReceived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await _fixture.Ldk.Bolt12ReceiveAsync("nltg pays ldk offer", 10_000, ct);
        Console.WriteLine($"[ldk] offer: {offer.ToJsonString()}");
        var balanceBefore = (await Session.GetOurChannelAsync(ct)).LocalBalance;

        // Act
        var response = await PayAsync(new PayOfferClientRequest(offer["offer"]!.GetValue<string>())
        {
            PayerNote = "nltg-ldk-note"
        }, ct);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, response.Fetch.Status);
        Assert.True(response.Fetch.PathCount > 0, "LDK's invoice has no blinded path");
        Assert.NotNull(response.Payment);
        Assert.True(response.Payment.Status == PaymentStatus.Succeeded, response.Payment.FailureReason);
        Assert.Equal(10_000_000UL, response.Payment.Amount.MilliSatoshi);
        Assert.NotNull(response.Payment.Preimage);
        var received = await WaitLdkReceivedAsync(offer["offer_id"]!.GetValue<string>(), ct);
        Assert.Equal(10_000_000L, received["amount_msat"]!.GetValue<long>());
        Assert.Equal("nltg-ldk-note", LdkChannelSession.FindString(received, "payer_note"));
        Assert.Equal(Convert.ToHexString((byte[])response.Payment.Preimage.Value),
                     LdkChannelSession.FindString(received, "preimage"), ignoreCase: true);
        await Session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = (await Session.GetOurChannelAsync(ct)).LocalBalance;
        Assert.Equal(balanceBefore.MilliSatoshi - 10_000_000 - response.Payment.Fee.MilliSatoshi, after.MilliSatoshi);
    }

    /// <summary>(b) LDK's amountless offer paid with 7,654 sat of our choice.</summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnAmountlessLdkOffer_When_WePayItWithAnAmount_Then_LdkReceivesThatAmount()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await _fixture.Ldk.Bolt12ReceiveAsync("nltg donation to ldk", null, ct);

        // Act
        var response = await PayAsync(new PayOfferClientRequest(offer["offer"]!.GetValue<string>())
        {
            Amount = LightningMoney.Satoshis(7_654)
        }, ct);

        // Assert
        Assert.True(response.Payment?.Status == PaymentStatus.Succeeded,
                    response.Payment?.FailureReason ?? response.Fetch.Error);
        var received = await WaitLdkReceivedAsync(offer["offer_id"]!.GetValue<string>(), ct);
        Assert.Equal(7_654_000L, received["amount_msat"]!.GetValue<long>());
    }

    /// <summary>
    /// (c) Our offer of 9,000 sat (with <c>offer_paths</c> through LDK): LDK's <c>pay</c> fetches our invoice and pays it
    /// over our blinded paths; our invoice is Settled for at least the amount and LDK's payment carries our preimage.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurOfferWithAnAmount_When_LdkPaysIt_Then_OurInvoiceIsSettled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await CreateOfferAsync(new CreateOfferClientRequest
        {
            Amount = LightningMoney.Satoshis(9_000),
            Description = "ldk pays nltg offer"
        }, ct);
        Assert.True(offer.HasPaths, "a node with only private channels must put offer_paths in its offers");
        var balanceBefore = (await Session.GetOurChannelAsync(ct)).LocalBalance;

        // Act
        var paid = await _fixture.Ldk.PayOfferAsync(offer.Bolt12, null, PayTimeoutSeconds, ct);

        // Assert
        var ours = await AssertSettledFromLdkAsync(paid, 9_000_000UL, ct);
        Assert.Equal(offer.OfferId, ours.OfferId);
        await Session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = (await Session.GetOurChannelAsync(ct)).LocalBalance;
        Assert.Equal(balanceBefore.MilliSatoshi + ours.AmountReceived!.MilliSatoshi, after.MilliSatoshi);
    }

    /// <summary>(d) Our amountless offer: LDK pays 4,321 sat of its choice.</summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurAmountlessOffer_When_LdkPaysItWithAnAmount_Then_SettledForThatAmount()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var offer = await CreateOfferAsync(new CreateOfferClientRequest { Description = "ldk tips nltg" }, ct);

        // Act
        var paid = await _fixture.Ldk.PayOfferAsync(offer.Bolt12, 4_321_000, PayTimeoutSeconds, ct);

        // Assert
        await AssertSettledFromLdkAsync(paid, 4_321_000UL, ct);
    }

    /// <summary>
    /// (e) Keysend both ways: our <c>keysend</c> of 12,345 sat with a custom record reaches LDK (an inbound spontaneous
    /// payment with our preimage), and LDK's <c>spontaneous-send</c> of 7,777 sat with a custom record is a settled
    /// keysend record of ours with that record.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_TheChannel_When_KeysendBothWaysWithCustomRecords_Then_BothSettle()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var record = "{\"app_name\":\"nltg\"}"u8.ToArray();

        // Act 1: we keysend LDK
        PayInvoiceClientResponse ours;
        using (var scope = Session.Node.Services.CreateScope())
        {
            ours = await scope.ServiceProvider
                              .GetRequiredService<IClientCommandHandler<KeysendClientRequest, PayInvoiceClientResponse>>()
                              .HandleAsync(new KeysendClientRequest(Session.LdkPubKey, LightningMoney.Satoshis(12_345))
                              {
                                  CustomRecords = [new CustomRecord(CustomRecordType, record)],
                                  TimeoutSeconds = PayTimeoutSeconds
                              }, ct);
        }

        // Assert 1
        Console.WriteLine($"[ldk] our keysend: {ours.Payment.Status} {ours.Payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, ours.Payment.Status);
        Assert.True(ours.Payment.IsKeysend);
        var hashHex = Convert.ToHexString((byte[])ours.Payment.PaymentHash).ToLowerInvariant();
        var theirs = await Poll.ForAsync(async () =>
        {
            var payment = await FindLdkPaymentAsync(p => LdkChannelSession.FindString(p, "hash") is { } h
                                                       && string.Equals(h, hashHex,
                                                                        StringComparison.OrdinalIgnoreCase), ct);
            return string.Equals(LdkClient.StatusOf(payment), "SUCCEEDED", StringComparison.OrdinalIgnoreCase)
                       ? payment
                       : null;
        }, s_timeout, "LDK lists our keysend as received", ct);
        Console.WriteLine($"[ldk] LDK received the keysend: {theirs.ToJsonString()}");
        Assert.Equal(12_345_000L, theirs["amount_msat"]!.GetValue<long>());
        Assert.Equal("INBOUND", theirs["direction"]!.ToString(), ignoreCase: true);

        // Act 2: LDK keysends us
        var paymentId = await _fixture.Ldk.SpontaneousSendAsync(Session.Node.NodeIdHex, 7_777_000, ct,
                                                                $"{CustomRecordType}:{Convert.ToHexString(record)}");
        Console.WriteLine($"[ldk] LDK's keysend payment id {paymentId}");

        // Assert 2: LDK's payment succeeded and our keysend record carries LDK's custom record
        var sent = await Poll.ForAsync(async () =>
        {
            var payment = await _fixture.Ldk.GetPaymentAsync(paymentId, ct);
            var status = LdkClient.StatusOf(payment);
            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
                Assert.Fail($"LDK's keysend failed: {payment!.ToJsonString()}");
            return string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase) ? payment : null;
        }, s_timeout, "LDK's keysend succeeded", ct);
        Console.WriteLine($"[ldk] LDK's keysend: {sent.ToJsonString()}");
        var ldkPreimage = Convert.FromHexString(LdkChannelSession.FindString(sent, "preimage")
                                             ?? throw new InvalidOperationException("LDK reported no preimage"));
        var stored = await Poll.ForAsync(async () => await Session.Node.GetInvoiceAsync(
                                                         new Hash(SHA256.HashData(ldkPreimage)), ct) is
        { Status: InvoiceStatus.Settled } i
                                                         ? i
                                                         : null,
                                         s_timeout, "our keysend record settled", ct);
        Assert.Equal(InvoiceKind.Keysend, stored.Kind);
        Assert.Equal(LightningMoney.Satoshis(7_777), stored.AmountReceived);
        var custom = Assert.Single(stored.CustomRecords, r => r.Type == CustomRecordType);
        Assert.Equal(record, custom.Value.ToArray());
        await Session.WaitUsableAsync(ct, requireNoHtlcs: true);
    }

    private async Task<PayOfferClientResponse> PayAsync(PayOfferClientRequest request, CancellationToken ct)
    {
        using var scope = Session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<PayOfferClientRequest, PayOfferClientResponse>>();
        var response = await handler.HandleAsync(request, ct);
        Console.WriteLine($"[ldk] payoffer: fetch {response.Fetch.Status} after {response.Fetch.Attempts} request(s)"
                        + $" ({response.Fetch.Error}), invoice node {response.Fetch.NodeId}, "
                        + $"{response.Fetch.PathCount} path(s); payment {response.Payment?.Status} "
                        + $"({response.Payment?.FailureReason}), fee {response.Payment?.Fee.MilliSatoshi} msat");
        return response;
    }

    private async Task<OfferInfoClientResponse> CreateOfferAsync(CreateOfferClientRequest request,
                                                                 CancellationToken ct)
    {
        using var scope = Session.Node.Services.CreateScope();
        var response = await scope.ServiceProvider
                                  .GetRequiredService<IClientCommandHandler<CreateOfferClientRequest,
                                       CreateOfferClientResponse>>()
                                  .HandleAsync(request, ct);
        Console.WriteLine($"[nltg] offer {response.Offer.OfferId}: {response.Offer.Bolt12}");
        return response.Offer;
    }

    /// <summary>
    /// LDK's <c>pay --wait</c> of our offer succeeded with a preimage whose invoice of ours is Settled for at least
    /// <paramref name="amountMsat"/> (plus at most our dummy hops' fee, which LDK pays inside the path's pay info).
    /// </summary>
    private async Task<InvoiceInfoClientResponse> AssertSettledFromLdkAsync(JsonNode paid, ulong amountMsat,
                                                                            CancellationToken ct)
    {
        Console.WriteLine($"[ldk] LDK pay: {paid.ToJsonString()}");
        var details = paid["payment"] ?? paid;
        Assert.Equal("SUCCEEDED", LdkClient.StatusOf(details), StringComparer.OrdinalIgnoreCase);
        var preimage = Convert.FromHexString(LdkChannelSession.FindString(details, "preimage")
                                          ?? throw new InvalidOperationException("LDK reported no preimage"));
        var ours = await Poll.ForAsync(async () => await Session.Node.GetInvoiceAsync(
                                                       new Hash(SHA256.HashData(preimage)), ct) is
        { Status: InvoiceStatus.Settled } i
                                                       ? i
                                                       : null,
                                       s_timeout, "our invoice settled", ct);
        Console.WriteLine($"[nltg] received {ours.AmountReceived?.MilliSatoshi} msat for {amountMsat} msat");
        Assert.Equal(InvoiceKind.Bolt12, ours.Kind);
        Assert.InRange(ours.AmountReceived!.MilliSatoshi, amountMsat, amountMsat + 10_000);
        return ours;
    }

    /// <summary>LDK's inbound payment for <paramref name="offerId"/> once it succeeded.</summary>
    private async Task<JsonNode> WaitLdkReceivedAsync(string offerId, CancellationToken ct)
    {
        var last = "nothing";
        try
        {
            return await Poll.ForAsync(async () =>
            {
                var payment = await FindLdkPaymentAsync(p => LdkChannelSession.FindString(p, "offer_id") == offerId
                                                          && string.Equals(p["direction"]?.ToString(), "INBOUND",
                                                                           StringComparison.OrdinalIgnoreCase), ct);
                last = payment?.ToJsonString() ?? "not listed";
                return string.Equals(LdkClient.StatusOf(payment), "SUCCEEDED", StringComparison.OrdinalIgnoreCase)
                           ? payment
                           : null;
            }, s_timeout, "LDK lists the offer's payment as received", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}. LDK's payment: {last}", e);
        }
    }

    private async Task<JsonNode?> FindLdkPaymentAsync(Func<JsonNode, bool> predicate, CancellationToken ct)
    {
        var payments = await _fixture.Ldk.RunAsync("list-payments", ct, "-n", "1000");
        return (payments["list"]?.AsArray() ?? []).OfType<JsonNode>().LastOrDefault(predicate);
    }
}