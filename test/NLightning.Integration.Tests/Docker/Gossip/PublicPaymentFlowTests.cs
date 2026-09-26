using System.Globalization;
using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Application.Payments.Invoices;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using Utils;

/// <summary>
/// The BOLT 7 goal proofs against LND 0.20: paying and getting paid over public channels with no route hints (plan
/// §5 G4, gossip wave G-C lane C3). Our node has one public channel to alice; the fixture's LND channels
/// (alice-bob twice, alice-carol, bob-carol) are public; nothing is hinted, so every route comes from a graph.
/// (b) we pay carol's hint-free invoice: our pathfinder finds us → alice → … → carol, every LND forward charged
/// exactly its announced fee, our reported fee is their sum and our balance moves by amount + fee;
/// (c) carol, who has no channel to us, pays our hint-free invoice: LND routes to us through the graph (our
/// announcement), every hop at its announced fee, and we receive exactly the amount;
/// (d) our <c>getroute</c> (IPC 19) equals the route LND computes for us (<c>QueryRoutes</c> with our node as source):
/// the same channels, amounts and total fee, and <c>payinvoice</c> pays exactly that fee.
/// </summary>
/// <remarks>
/// <para>Each test uses its own node and channel and asserts only on them; fees are computed from the policies LND
/// announced at payment time (a fresh fixture charges LND's defaults, but another test of the collection may have
/// changed a policy and restored it), never from constants. Amounts are unique per test so the forwards can be traced
/// by amount through <c>ForwardingHistory</c>.</para>
/// <para>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture). Needs lane C2 (G4: graph paths in
/// <c>PaymentService</c>, <c>getroute</c> = IPC 19 through <see cref="GetRouteProbe"/>, no route hints in our invoices
/// once an announced channel can receive); not lane C1 (our graph is filled through alice's dump after a hand-sent
/// <c>gossip_timestamp_filter</c>, <see cref="PublicTopology.SyncOurGraphAsync"/>) and not NL-348 (we never forward).
/// Both payees are checked to have no channel with our node.</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class PublicPaymentFlowTests
{
    private const ulong AmountBaseMsat = 25_000_000;
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_routeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_hintGracePeriod = TimeSpan.FromSeconds(5);

    private readonly LightningRegtestNetworkFixture _fixture;

    public PublicPaymentFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    /// <summary>Goal proof (b), plan Proof G4 (a).</summary>
    [Fact]
    public async Task Given_PublicChannelToAlice_When_WePayCarolsInvoiceWithoutHints_Then_RoutedAtTheAnnouncedFees()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var carol = _fixture.GetLndNode("carol");
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(alice, ct)}");
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-pay-b", "nltg-pay-b", ct);
        var channel = await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, null, [alice], ct);
        var amountMsat = PublicTopology.UniqueAmountMsat(AmountBaseMsat);
        await LndRoutingProbe.AssertNoChannelWithAsync(carol, node.NodeIdHex, ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(carol, (long)amountMsat, [], ct, "goal (b)");
        await LndRoutingProbe.AssertNoRouteHintsAsync(carol, invoice.PaymentRequest, ct);
        var before = await PublicTopology.WaitSettledAsync(node, channel.ChannelId, ct);
        var since = DateTimeOffset.UtcNow;

        // Act
        var payment = await PublicTopology.PayInOnePartAsync(node, invoice.PaymentRequest, ct);

        // Assert: paid, and carol got exactly the amount
        Console.WriteLine($"Our payment: {payment.Status}, amount {payment.Amount.MilliSatoshi}, fee "
                        + $"{payment.Fee.MilliSatoshi}, failure {payment.FailureCode} at {payment.FailureSourceIndex}: "
                        + $"{payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var settled = await LndTestHelpers.WaitForInvoiceStateAsync(carol, invoice.RHash.ToByteArray(),
                                                                     Invoice.Types.InvoiceState.Settled,
                                                                     s_settleTimeout, ct);
        Assert.Equal((long)amountMsat, settled.AmtPaidMsat);
        Assert.Equal(amountMsat, payment.Amount.MilliSatoshi);

        // Assert: every LND forward, traced back from carol to our channel, charged its announced fee, and our fee
        // is their sum
        var chain = await LndRoutingProbe.WaitForForwardChainAsync(PublicTopology.LndNodes(_fixture), since,
                                                                    amountMsat, channel.ShortChannelId, ct);
        Assert.Equal(alice.LocalNodePubKey.ToLowerInvariant(), chain[^1].NodeIdHex);
        var fees = await LndRoutingProbe.AssertForwardFeesMatchPoliciesAsync(alice, chain, ct);
        Assert.Equal(fees, payment.Fee.MilliSatoshi);

        // Assert: our balance moved by exactly amount + fee
        var after = await PublicTopology.WaitSettledAsync(node, channel.ChannelId, ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi - amountMsat - fees, after.LocalBalance.MilliSatoshi);
    }

    /// <summary>Goal proof (c).</summary>
    [Fact]
    public async Task Given_PublicChannelToAlice_When_CarolPaysOurInvoiceWithoutHints_Then_WeReceiveTheExactAmount()
    {
        // Arrange: pushed, so alice can send over our channel; carol has no channel to us
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var carol = _fixture.GetLndNode("carol");
        // Node:Invoices:RouteHints stays Auto (the default); only its grace period (10 min by default: how long our
        // announced channel must be in our graph with both policies before hints are left out) is shortened
        await using var node = await GossipTestNodes.StartGossipNodeAsync(
                                   _fixture, "gossip-pay-c", "nltg-pay-c", ct,
                                   n => n.ExtraConfiguration[$"{InvoiceOptions.SectionName}:"
                                                           + $"{nameof(InvoiceOptions.PublicChannelGracePeriod)}"] =
                                            s_hintGracePeriod.ToString("c", CultureInfo.InvariantCulture));
        var channel = await PublicTopology.OpenPublicChannelToAliceAsync(
                          _fixture, node, LightningMoney.Satoshis(400_000), [alice, carol], ct);
        await Poll.ForAsync(() => GossipGraphProbe.TryGetNodeInfoAsync(carol, node.NodeIdHex, ct), s_routeTimeout,
                            "carol has our node_announcement", ct, GossipGraphProbe.PollInterval);
        var amountMsat = PublicTopology.UniqueAmountMsat(AmountBaseMsat);
        // No hint once our announced channel can receive the payment and has been in our graph for the grace period
        var invoice = await Poll.ForAsync(async () =>
        {
            var created = await node.CreateInvoiceAsync(LightningMoney.MilliSatoshis(amountMsat), "goal (c)", ct);
            var decoded = await carol.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = created.Bolt11 },
                                                                        cancellationToken: ct);
            Console.WriteLine($"Our invoice {created.Bolt11}: {decoded.RouteHints.Count} route hints");
            return decoded.RouteHints.Count == 0 ? created : null;
        }, s_routeTimeout, "our invoice without route hints", ct, TimeSpan.FromSeconds(2));
        await LndRoutingProbe.AssertNoRouteHintsAsync(carol, invoice.Bolt11, ct);
        await LndRoutingProbe.AssertNoChannelWithAsync(carol, node.NodeIdHex, ct);
        var before = await PublicTopology.WaitSettledAsync(node, channel.ChannelId, ct);

        // Act: one part, any route LND finds (retried while LND's router lacks the fresh edge, NL-319)
        var payment = await Poll.ForAsync(async () =>
        {
            var result = await LndTestHelpers.SendPaymentV2Async(
                             carol, LndTestHelpers.PinnedPayment(invoice.Bolt11, []), ct);
            Console.WriteLine($"carol's payment: {result.Status} {result.FailureReason}");
            return result.Status == Payment.Types.PaymentStatus.Succeeded
                   || result.FailureReason != PaymentFailureReason.FailureReasonNoRoute
                       ? result
                       : null;
        }, s_routeTimeout, "carol's payment to us final (not no_route)", ct, TimeSpan.FromSeconds(5));

        // Assert: LND reached us over our public channel, every hop at its announced fee
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        var route = payment.Htlcs.Single(h => h.Status == HTLCAttempt.Types.HTLCStatus.Succeeded).Route;
        Console.WriteLine($"carol's route: {LndRoutingProbe.Describe(route)}");
        Assert.Equal(node.NodeIdHex, route.Hops[^1].PubKey, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(channel.ShortChannelId, route.Hops[^1].ChanId);
        Assert.Equal((long)amountMsat, route.Hops[^1].AmtToForwardMsat);
        var fees = await LndRoutingProbe.AssertRouteFeesMatchPoliciesAsync(carol, route, ct);
        Assert.Equal((long)fees, payment.FeeMsat);

        // Assert: our invoice settled for the amount and our balance grew by exactly that
        var ours = await Poll.ForAsync(async () =>
        {
            var current = await node.GetInvoiceAsync(invoice.PaymentHash, ct);
            return current?.Status == InvoiceStatus.Settled ? current : null;
        }, s_settleTimeout, "our invoice settled", ct);
        Assert.Equal(amountMsat, ours.AmountReceived?.MilliSatoshi);
        var after = await PublicTopology.WaitSettledAsync(node, channel.ChannelId, ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + amountMsat, after.LocalBalance.MilliSatoshi);
    }

    /// <summary>Goal proof (d), plan Proof G4 (d).</summary>
    [Fact]
    public async Task Given_PublicChannelToAlice_When_GettingARouteToCarol_Then_ItEqualsLndsRouteAndThePaidFee()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var carol = _fixture.GetLndNode("carol");
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-pay-d", "nltg-pay-d", ct);
        var channel = await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, null, [alice], ct);
        var amountMsat = PublicTopology.UniqueAmountMsat(AmountBaseMsat);
        var carolId = new CompactPubKey(carol.LocalNodePubKeyBytes);

        // Act: our route, then LND's for the same source, destination and amount
        var ours = await GetRouteProbe.WaitForRouteAsync(node, carolId, amountMsat, r => r.Found, s_routeTimeout,
                                                         "getroute to carol found", ct);
        var lnd = await LndRoutingProbe.QueryRouteAsync(alice, node.NodeIdHex, carol.LocalNodePubKey,
                                                        (long)amountMsat, ct);

        // Assert: same channels, first ours, every node and hop as LND has it, every LND fee as announced
        Assert.Equal(channel.ShortChannelId, ours.Hops[0].ShortChannelId);
        Assert.Equal(lnd.Hops.Select(h => h.ChanId), ours.ShortChannelIds);
        Assert.Equal(lnd.Hops.Select(h => h.PubKey.ToLowerInvariant()), ours.Hops.Select(h => h.NodeIdHex));
        Assert.Equal(carol.LocalNodePubKey.ToLowerInvariant(), ours.Hops[^1].NodeIdHex);
        var lndFees = await LndRoutingProbe.AssertRouteFeesMatchPoliciesAsync(alice, lnd, ct);
        Assert.Equal(lndFees, ours.TotalFeeMsat);
        // getroute's hop amount is what the HTLC arriving at the hop's node carries, its fee what that node keeps
        Assert.Equal(LndRoutingProbe.ChannelAmounts(lnd), ours.Hops.Select(h => h.AmountMsat));
        Assert.Equal(lnd.Hops.Select(h => (ulong)h.FeeMsat), ours.Hops.Select(h => h.FeeMsat));
        Assert.Equal(amountMsat, ours.Hops[^1].AmountMsat);

        // Act 2: pay carol's hint-free invoice for that amount
        var invoice = await LndTestHelpers.AddInvoiceAsync(carol, (long)amountMsat, [], ct, "goal (d)");
        await LndRoutingProbe.AssertNoRouteHintsAsync(carol, invoice.PaymentRequest, ct);
        var before = await PublicTopology.WaitSettledAsync(node, channel.ChannelId, ct);
        var payment = await PublicTopology.PayInOnePartAsync(node, invoice.PaymentRequest, ct);

        // Assert 2: paid exactly the fee getroute and LND computed
        Console.WriteLine($"Our payment: {payment.Status}, fee {payment.Fee.MilliSatoshi}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(lndFees, payment.Fee.MilliSatoshi);
        var after = await PublicTopology.WaitSettledAsync(node, channel.ChannelId, ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi - amountMsat - lndFees, after.LocalBalance.MilliSatoshi);
    }
}