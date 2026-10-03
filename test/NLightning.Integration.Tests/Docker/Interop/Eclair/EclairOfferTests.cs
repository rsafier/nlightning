using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
using Daemon.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Payments.Enums;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 12 offers and BOLT 4 onion messages against Eclair 0.14.3 (NL-554), on the dual-funded private channel we
/// open with a plain <c>openchannel</c>: (a) Eclair pays our offer (<c>payoffer</c>): its <c>invoice_request</c> comes
/// over our offer path (Eclair is its introduction node), our invoice goes back as an onion message, and Eclair pays it
/// through our blinded payment path; (b) we pay Eclair's offer (<c>createoffer</c>, our <c>payoffer</c>): our
/// <c>invoice_request</c> reaches Eclair as an onion message, its invoice comes back, and our payment through its
/// blinded path succeeds and Eclair lists it received.
/// </summary>
/// <remarks>Run with <c>scripts/run-cluster.sh -n 1 --suite eclair --class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairOfferTests</c>.</remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairOfferTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;

    public EclairOfferTests(EclairFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        if (TestDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            await _fixture.DumpEclairLogAsync(400);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) Our offer of 25,000 sat (with <c>offer_paths</c>: our only channel is private); Eclair's <c>payoffer</c>
    /// fetches our invoice over onion messages and pays it: Eclair reports the payment sent with the preimage of our
    /// invoice, which is Settled for at least the amount, and our channel balance grew by what we received.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurOffer_When_EclairPaysIt_Then_OurInvoiceIsSettled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await BuildAsync("nltg-eclair-offer-a", ct);
        var created = await HandleAsync<CreateOfferClientRequest, CreateOfferClientResponse>(
                          session, new CreateOfferClientRequest
                          {
                              Amount = LightningMoney.Satoshis(25_000),
                              Description = "nltg offer paid by eclair"
                          }, ct);
        var offer = created.Offer;
        Console.WriteLine($"[nltg] offer {offer.Bolt12} (paths: {offer.HasPaths})");
        Assert.True(offer.HasPaths, "a node with only private channels must put offer_paths in its offers");
        var before = await session.GetOurChannelAsync(ct);

        // Act
        var result = await session.Eclair.PayOfferAsync(offer.Bolt12, 25_000_000, ct);

        // Assert
        Console.WriteLine($"[eclair] payoffer: {result.ToJsonString()}");
        Assert.Equal("payment-sent", result["type"]?.GetValue<string>());
        var hash = Convert.FromHexString(result["paymentHash"]!.GetValue<string>());
        var preimage = Convert.FromHexString(result["paymentPreimage"]!.GetValue<string>());
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(preimage));
        var ours = await Poll.ForAsync(async () => await session.Node.GetInvoiceAsync(hash, ct) is
        { Status: InvoiceStatus.Settled } invoice
                                                       ? invoice
                                                       : null,
                                       s_settleTimeout, "our offer's invoice settled", ct);
        Console.WriteLine($"[proof] our invoice settled for {ours.AmountReceived?.MilliSatoshi} msat");
        Assert.NotNull(ours.AmountReceived);
        Assert.True(ours.AmountReceived.MilliSatoshi >= 25_000_000UL);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + ours.AmountReceived.MilliSatoshi,
                     after.LocalBalance.MilliSatoshi);
    }

    /// <summary>
    /// (b) Eclair's <c>createoffer</c> of 15,000 sat (its node id as <c>offer_issuer_id</c>, no paths); our
    /// <c>payoffer</c> fetches the invoice over an onion message to Eclair and pays it: our payment succeeded with the
    /// invoice's node id, and Eclair lists the payment received.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnEclairOffer_When_WePayIt_Then_EclairListsItReceived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await BuildAsync("nltg-eclair-offer-b", ct);
        var created = await session.Eclair.CreateOfferAsync("eclair offer paid by nltg", 15_000_000, ct);
        Console.WriteLine($"[eclair] createoffer: {created.ToJsonString()}");
        var bolt12 = FindOffer(created) ?? throw new InvalidOperationException("No lno1 string in Eclair's offer");

        // Act
        var response = await HandleAsync<PayOfferClientRequest, PayOfferClientResponse>(
                           session, new PayOfferClientRequest(bolt12), ct);

        // Assert
        Console.WriteLine($"[nltg] payoffer: fetch {response.Fetch.Status} ({response.Fetch.Error}), "
                        + $"{response.Fetch.PathCount} path(s); payment {response.Payment?.Status} "
                        + $"({response.Payment?.FailureReason}), fee {response.Payment?.Fee.MilliSatoshi} msat");
        Assert.Equal(FetchInvoiceStatus.Received, response.Fetch.Status);
        Assert.NotNull(response.Payment);
        Assert.True(response.Payment.Status == PaymentStatus.Succeeded, response.Payment.FailureReason);
        Assert.Equal(15_000_000UL, response.Payment.Amount.MilliSatoshi);
        Assert.Equal(_fixture.EclairNodeId, Convert.ToHexStringLower((byte[])response.Payment.PayeeNodeId));
        var hash = Convert.ToHexStringLower((byte[])response.Payment.PaymentHash);
        var received = await Poll.ForAsync(async () =>
        {
            var info = await session.Eclair.GetReceivedInfoAsync(hash, ct);
            return info?["status"]?["type"]?.GetValue<string>() == "received" ? info : null;
        }, s_settleTimeout, "Eclair lists the offer payment as received", ct);
        Console.WriteLine($"[proof] Eclair received: {received.ToJsonString()}");
    }

    /// <summary>
    /// The dual-funded channel we open to Eclair (plain <c>openchannel</c>, 1M sat) with 200k sat paid to Eclair so it
    /// can pay us.
    /// </summary>
    private async Task<EclairChannelSession> BuildAsync(string nodeName, CancellationToken ct)
    {
        _session = await EclairChannelSession.BuildOurFundedAsync(_fixture, nodeName, s_capacity, null, ct);
        await _session.AssertWePayEclairAsync(LightningMoney.Satoshis(200_000), ct);
        return _session;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(EclairChannelSession session,
                                                                         TRequest request, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    /// <summary>The first <c>lno1...</c> string anywhere in Eclair's answer.</summary>
    private static string? FindOffer(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) && text.StartsWith("lno1", StringComparison.Ordinal)
            => text,
        JsonObject obj => obj.Select(p => FindOffer(p.Value)).FirstOrDefault(s => s is not null),
        JsonArray array => array.Select(FindOffer).FirstOrDefault(s => s is not null),
        _ => null
    };
}