namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Harness;

/// <summary>
/// LND <c>QueryRoutes</c> semantics over the planner (NL-1242), on the graph harness Bob → Carol → David → Erin:
/// the exact final expiry, ignored nodes and directed pairs, allowed first-hop channels and a fixed last hop.
/// </summary>
public class QueryRouteHarnessTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);

    private static PaymentHarness Harness(bool secondBobCarol = false, bool secondCarolDavid = false)
    {
        var harness = new PaymentHarness(new PaymentHarnessTopology(SecondBobCarol: secondBobCarol,
                                                                    SecondCarolDavid: secondCarolDavid, Erin: true,
                                                                    BobUsesGraph: true));
        harness.Bob.GraphView = harness.BuildGraph((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return harness;
    }

    private static RouteQueryRequest Query(CompactPubKey payee) =>
        new(payee, s_amount)
        {
            MaxFee = LightningMoney.Satoshis(1_000),
            FinalCltvDelta = 40
        };

    [Fact]
    public async Task Given_ErinThroughTheGraph_When_Queried_Then_TheFinalExpiryIsExactlyTheHeightPlusTheDelta()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;

        // Act
        var quote = await harness.Bob.RouteQuery.QueryRouteAsync(Query(harness.Erin!.NodeId), ct);

        // Assert: no safety blocks (LND's QueryRoutes adds no padding), each hop's delta above it
        var route = quote.Route;
        Assert.Equal([harness.Carol.NodeId, harness.David.NodeId, harness.Erin!.NodeId],
                     route.Hops.Select(h => h.NodeId));
        Assert.Equal(PaymentHarness.BlockHeight + 40, route.Hops[^1].OutgoingCltvValue);
        Assert.Equal(PaymentHarness.BlockHeight + 40 + 40 + 40, route.FirstHopCltvExpiry);
        Assert.Equal(s_amount, route.Amount);
        Assert.Equal(harness.BobCarol, quote.Channel.ChannelId);
    }

    [Fact]
    public async Task Given_TheCarolToDavidPairIgnored_When_Queried_Then_NoRouteButTheReverseDirectionStaysUsable()
    {
        // Arrange
        using var harness = Harness(secondCarolDavid: true);
        var ct = TestContext.Current.CancellationToken;
        var forward = Query(harness.Erin!.NodeId) with
        {
            IgnoredPairs = [(harness.Carol.NodeId, harness.David.NodeId)]
        };
        var reverse = Query(harness.Erin!.NodeId) with
        {
            IgnoredPairs = [(harness.David.NodeId, harness.Carol.NodeId)]
        };

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Bob.RouteQuery.QueryRouteAsync(forward, ct));
        var quote = await harness.Bob.RouteQuery.QueryRouteAsync(reverse, ct);

        // Assert: both Carol–David channels skipped in that direction only
        Assert.StartsWith("No route", refused.Message);
        Assert.Equal(harness.Erin!.NodeId, quote.Route.PayeeNodeId);
    }

    [Fact]
    public async Task Given_DavidIgnored_When_Queried_Then_NoRoute()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var query = Query(harness.Erin!.NodeId) with
        {
            IgnoredNodes = new HashSet<CompactPubKey> { harness.David.NodeId }
        };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Bob.RouteQuery.QueryRouteAsync(query, ct));
    }

    [Fact]
    public async Task Given_OnlyTheSecondBobCarolChannelAllowed_When_Queried_Then_TheRouteLeavesThroughIt()
    {
        // Arrange
        using var harness = Harness(secondBobCarol: true);
        var ct = TestContext.Current.CancellationToken;
        var query = Query(harness.Erin!.NodeId) with
        {
            OutgoingChannels = new HashSet<ShortChannelId> { PaymentHarness.ScidBobCarol2 }
        };

        // Act
        var quote = await harness.Bob.RouteQuery.QueryRouteAsync(query, ct);

        // Assert
        Assert.Equal(harness.BobCarol2, quote.Channel.ChannelId);
        Assert.Equal(PaymentHarness.ScidBobCarol2, quote.Channel.ShortChannelId);
    }

    [Fact]
    public async Task Given_DavidAsTheLastHop_When_Queried_Then_TheRouteEndsOverHisChannelToErinWithItsFee()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var plain = await harness.Bob.RouteQuery.QueryRouteAsync(Query(harness.Erin!.NodeId), ct);
        var query = Query(harness.Erin!.NodeId) with { LastHop = harness.David.NodeId };

        // Act
        var quote = await harness.Bob.RouteQuery.QueryRouteAsync(query, ct);

        // Assert: the same route as without the constraint, the last edge appended from the graph
        Assert.Equal(plain.Route.Hops.Select(h => (h.NodeId, h.AmountToForward, h.OutgoingCltvValue,
                                                   h.OutgoingShortChannelId)),
                     quote.Route.Hops.Select(h => (h.NodeId, h.AmountToForward, h.OutgoingCltvValue,
                                                   h.OutgoingShortChannelId)));
        Assert.Equal(plain.Route.FirstHopAmount, quote.Route.FirstHopAmount);
        Assert.Equal(plain.Route.FirstHopCltvExpiry, quote.Route.FirstHopCltvExpiry);
        Assert.Equal(PaymentHarness.ScidDavidErin, quote.Route.Hops[^2].OutgoingShortChannelId);
    }

    [Fact]
    public async Task Given_CarolAsTheLastHop_When_SheHasNoChannelToErin_Then_NoRoute()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var query = Query(harness.Erin!.NodeId) with { LastHop = harness.Carol.NodeId };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Bob.RouteQuery.QueryRouteAsync(query, ct));
    }

    [Fact]
    public async Task Given_AQueriedRoute_When_PaidAsGivenWithTheInvoicesSecret_Then_ErinIsPaid()
    {
        // Arrange: bos's flow: QueryRoutes with the invoice's delta + 6, then SendToRouteV2 (payroute's raw form)
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Erin!.InvoiceService.CreateInvoiceAsync(s_amount, "bos", null, ct);
        var quote = await harness.Bob.RouteQuery.QueryRouteAsync(Query(harness.Erin.NodeId) with
        {
            FinalCltvDelta = 46
        }, ct);
        var route = quote.Route;
        var request = new PayRouteRequest
        {
            PaymentHash = invoice.PaymentHash,
            PaymentSecret = invoice.PaymentSecret,
            TotalAmount = s_amount,
            Routes =
            [
                new PayRouteRoute(quote.Channel.ChannelId, route.FirstHopAmount, route.FirstHopCltvExpiry,
                                  route.Hops.Select(h => new PayRouteHop(h.NodeId, h.OutgoingShortChannelId,
                                                                         h.AmountToForward, h.OutgoingCltvValue))
                                       .ToList())
            ]
        };

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayRouteAsync(
                                                request, new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(30) },
                                                ct));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal(route.Fee, result.Payment.Fee);
    }

    [Fact]
    public async Task Given_ACltvLimitBelowTheRoute_When_Queried_Then_NoRoute()
    {
        // Arrange: the route needs 120 blocks (40 at Erin, 40 at David and Carol each)
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var query = Query(harness.Erin!.NodeId) with { MaxTotalCltvDelta = 100 };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Bob.RouteQuery.QueryRouteAsync(query, ct));
    }
}