using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
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
/// <para>Run with <c>scripts/run-gossip.sh</c> (own process, own fixture). Needs G4 (graph paths in
/// <c>PaymentService</c>, <c>getroute</c>) and NL-348; written against the contracts in parallel with lanes C1/C2.</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class PublicPaymentFlowTests
{
    private const ulong AmountBaseMsat = 25_000_000;
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_routeTimeout = TimeSpan.FromMinutes(2);

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
        var forwards = await LndRoutingProbe.GetForwardsAsync(PublicTopology.LndNodes(_fixture), since, ct);
        var chain = LndRoutingProbe.TraceForwards(forwards, amountMsat, channel.ShortChannelId);
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
        await using var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, "gossip-pay-c", "nltg-pay-c", ct);
        var channel = await PublicTopology.OpenPublicChannelToAliceAsync(
                          _fixture, node, LightningMoney.Satoshis(400_000), [alice, carol], ct);
        await Poll.ForAsync(() => GossipGraphProbe.TryGetNodeInfoAsync(carol, node.NodeIdHex, ct), s_routeTimeout,
                            "carol has our node_announcement", ct, GossipGraphProbe.PollInterval);
        var amountMsat = PublicTopology.UniqueAmountMsat(AmountBaseMsat);
        var invoice = await node.CreateInvoiceAsync(LightningMoney.MilliSatoshis(amountMsat), "goal (c)", ct);
        Console.WriteLine($"Our invoice {invoice.Bolt11}");
        // TODO(G-C integrator): our InvoiceService hints every Open channel with the peer's update, public ones too
        // (NL-245); a public channel needs no r field, so this fails until it skips announced channels
        await LndRoutingProbe.AssertNoRouteHintsAsync(carol, invoice.Bolt11, ct);
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

        // Assert: same channels, first ours, every LND fee as announced, same total fee
        Assert.Equal(channel.ShortChannelId, ours.Hops[0].ShortChannelId);
        Assert.Equal(lnd.Hops.Select(h => (ulong?)h.ChanId), ours.ShortChannelIds);
        Assert.Equal(carol.LocalNodePubKey.ToLowerInvariant(), ours.Hops[^1].NodeIdHex);
        var lndFees = await LndRoutingProbe.AssertRouteFeesMatchPoliciesAsync(alice, lnd, ct);
        var ourTotalFee = ours.TotalFeeMsat ?? (ulong)ours.Hops.Sum(h => (decimal)(h.FeeMsat ?? 0));
        Assert.Equal(lndFees, ourTotalFee);
        AssertSameHopAmounts(ours, lnd);

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

    /// <summary>
    /// Our hops' amounts equal LND's, read either as what each channel carries or as what each hop's node forwards.
    /// </summary>
    /// <remarks>
    /// TODO(G-C integrator): pin the reading once lane C2's <c>getroute</c> hop amount is known and drop the other.
    /// </remarks>
    private static void AssertSameHopAmounts(RouteView ours, Route lnd)
    {
        if (ours.Hops.Any(h => h.AmountMsat is null))
        {
            Console.WriteLine("getroute reports no per-hop amount: only the total fee is compared");
            return;
        }

        var ourAmounts = ours.Hops.Select(h => h.AmountMsat!.Value).ToList();
        var channelAmounts = LndRoutingProbe.ChannelAmounts(lnd);
        var forwardAmounts = lnd.Hops.Select(h => (ulong)h.AmtToForwardMsat).ToList();
        Console.WriteLine($"Hop amounts: ours [{string.Join(", ", ourAmounts)}], LND per channel "
                        + $"[{string.Join(", ", channelAmounts)}], LND to forward [{string.Join(", ", forwardAmounts)}]");
        Assert.True(ourAmounts.SequenceEqual(channelAmounts) || ourAmounts.SequenceEqual(forwardAmounts),
                    "getroute's hop amounts match neither LND's channel amounts nor its amounts to forward");
    }
}