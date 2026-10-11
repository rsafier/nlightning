using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Interfaces;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Routing;

/// <summary>
/// NL-609 proof, in process with the production switches, payment service, Sphinx, commitments and SQLite
/// (<see cref="ThreeNodeHarness"/> with the Carol-Alice channel, so the nodes form a triangle): Bob pays his own invoice
/// over a circular route, out through one of his channels and back in through the other. Bob's switch settles the
/// invoice as the final hop of the incoming side while his payment service records the outgoing payment; the books
/// (the production posting rules) take only the route fee, as a rebalance expense, and no income.
/// </summary>
public class RebalanceHarnessTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Given_AnAllowedOutgoingChannelSet_When_Rebalancing_Then_EveryFirstHopBelongsToTheSet()
    {
        // Arrange
        await using var harness = await CreateAsync();
        GiveBobThePeerUpdate(harness, harness.Alice, ThreeNodeHarness.AliceBobChannelId);
        GiveBobThePeerUpdate(harness, harness.Carol, ThreeNodeHarness.BobCarolChannelId);
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "Loop channel set", null,
            TestContext.Current.CancellationToken);
        var allowed = new HashSet<Domain.Channels.ValueObjects.ChannelId>
            { ThreeNodeHarness.AliceBobChannelId, ThreeNodeHarness.BobCarolChannelId };
        // Act
        var result = await PayAsync(harness, invoice.Bolt11!, new PayInvoiceOptions
        {
            Timeout = s_timeout,
            OutgoingChannelIds = allowed,
            IncomingChannelId = ThreeNodeHarness.BobCarolChannelId
        });
        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(ThreeNodeHarness.AliceBobChannelId, result.Payment.OutgoingChannelId);
        Assert.Contains(result.Payment.OutgoingChannelId!.Value, allowed);
    }

    [Fact]
    public async Task Given_ATriangle_When_BobPaysHisOwnInvoice_Then_ItGoesOutToCarolAndBackFromAliceAsARebalance()
    {
        // Arrange: Bob knows Alice's policy on Alice-Bob (her channel_update) and Carol's on Carol-Alice (the graph)
        await using var harness = await CreateAsync();
        GiveBobThePeerUpdate(harness, harness.Alice, ThreeNodeHarness.AliceBobChannelId);
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "rebalance", null, ct);
        var aliceFee = harness.Alice.Options.Routing.CalculateFee(s_amount);
        var carolFee = harness.Carol.Options.Routing.CalculateFee(s_amount + aliceFee);
        var fee = aliceFee + carolFee;
        var bobAb = BalanceOf(harness.Bob, ThreeNodeHarness.AliceBobChannelId);
        var bobBc = BalanceOf(harness.Bob, ThreeNodeHarness.BobCarolChannelId);
        var bobTotal = harness.Bob.LocalBalanceMsat;

        // Act
        var result = await PayAsync(harness, invoice.Bolt11!, new PayInvoiceOptions { Timeout = s_timeout });

        // Assert: the payment succeeded with the invoice's preimage, out over Bob-Carol, back in over Alice-Bob
        var payment = result.Payment;
        Assert.True(payment.Status == PaymentStatus.Succeeded, payment.FailureReason);
        Assert.Equal(invoice.Preimage!.Value, payment.Preimage);
        Assert.Equal(fee, payment.Fee);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, payment.OutgoingChannelId);
        Assert.Equal([harness.Carol.NodeId, harness.Alice.NodeId, harness.Bob.NodeId],
                     payment.Route.Select(h => h.NodeId));
        Assert.Equal(harness.Bob.NodeId, payment.PayeeNodeId);

        // Bob's own invoice is settled for the amount (his switch, final hop of the incoming side)
        var stored = await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(
                                                        invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        Assert.Equal(s_amount, stored.AmountReceived);

        // The balance moved from Bob-Carol to Alice-Bob; Bob lost only the fee; Alice and Carol earned theirs
        Assert.Equal(bobBc - (long)(s_amount + fee).MilliSatoshi,
                     BalanceOf(harness.Bob, ThreeNodeHarness.BobCarolChannelId));
        Assert.Equal(bobAb + (long)s_amount.MilliSatoshi, BalanceOf(harness.Bob, ThreeNodeHarness.AliceBobChannelId));
        Assert.Equal(bobTotal - (long)fee.MilliSatoshi, harness.Bob.LocalBalanceMsat);
        Assert.All(harness.Nodes.SelectMany(n => n.Channels), c => Assert.Empty(c.Commitments!.Htlcs));

        // Bob's feed: the incoming settle and the outgoing payment, both flagged as a self-payment
        var events = await AccountingEventsAsync(harness.Bob);
        Assert.Equal(2, events.Count);
        var settled = Assert.Single(events, e => e.Kind == AccountingEventKind.InvoiceSettled);
        Assert.Equal((long)s_amount.MilliSatoshi, settled.AmountMsat);
        Assert.Equal(ThreeNodeHarness.AliceBobChannelId, settled.ChannelId);
        Assert.Equal(AccountingDetailKeys.True, settled.Details[AccountingDetailKeys.SelfPayment]);
        var paid = Assert.Single(events, e => e.Kind == AccountingEventKind.PaymentSucceeded);
        Assert.Equal(AccountingEventKeys.PaymentSucceeded(invoice.PaymentHash), paid.EventKey);
        Assert.Equal(-(long)(s_amount + fee).MilliSatoshi, paid.AmountMsat);
        Assert.Equal((long)fee.MilliSatoshi, paid.FeeMsat);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, paid.ChannelId);
        Assert.Equal(AccountingDetailKeys.True, paid.Details[AccountingDetailKeys.SelfPayment]);

        // The books: the channels account moved as the live balances (minus the fee), the fee is the rebalance cost,
        // and nothing is income, a payment sent or a routing fee
        var books = BooksSimulator.Of(events);
        Assert.Equal(harness.Bob.LocalBalanceMsat - bobTotal, books[AccountRole.Channels]);
        Assert.Equal((long)fee.MilliSatoshi, books[AccountRole.Rebalance]);
        Assert.Equal(0, books[AccountRole.Received]);
        Assert.Equal(0, books[AccountRole.Sent]);
        Assert.Equal(0, books[AccountRole.RoutingFees]);
        Assert.All(books.Entries, entry => Assert.True(entry.IsBalanced));

        // Carol and Alice each booked their forward fee
        var carolForward = Assert.Single(await AccountingEventsAsync(harness.Carol));
        Assert.Equal((AccountingEventKind.ForwardSettled, (long)carolFee.MilliSatoshi),
                     (carolForward.Kind, carolForward.AmountMsat));
        var aliceForward = Assert.Single(await AccountingEventsAsync(harness.Alice));
        Assert.Equal((AccountingEventKind.ForwardSettled, (long)aliceFee.MilliSatoshi),
                     (aliceForward.Kind, aliceForward.AmountMsat));
    }

    [Fact]
    public async Task Given_PinnedChannels_When_BobRebalancesTheOtherWay_Then_ItLeavesAndReturnsWhereItWasPinned()
    {
        // Arrange: Bob knows both peers' policies, and pins out = Alice-Bob, in = Bob-Carol
        await using var harness = await CreateAsync();
        GiveBobThePeerUpdate(harness, harness.Alice, ThreeNodeHarness.AliceBobChannelId);
        GiveBobThePeerUpdate(harness, harness.Carol, ThreeNodeHarness.BobCarolChannelId);
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "the other way", null,
                                                                    TestContext.Current.CancellationToken);
        var bobAb = BalanceOf(harness.Bob, ThreeNodeHarness.AliceBobChannelId);
        var bobBc = BalanceOf(harness.Bob, ThreeNodeHarness.BobCarolChannelId);

        // Act
        var result = await PayAsync(harness, invoice.Bolt11!, new PayInvoiceOptions
        {
            Timeout = s_timeout,
            OutgoingChannelId = ThreeNodeHarness.AliceBobChannelId,
            IncomingChannelId = ThreeNodeHarness.BobCarolChannelId
        });

        // Assert
        var payment = result.Payment;
        Assert.True(payment.Status == PaymentStatus.Succeeded, payment.FailureReason);
        Assert.Equal(ThreeNodeHarness.AliceBobChannelId, payment.OutgoingChannelId);
        Assert.Equal([harness.Alice.NodeId, harness.Carol.NodeId, harness.Bob.NodeId],
                     payment.Route.Select(h => h.NodeId));
        Assert.Equal(bobAb - (long)(s_amount + payment.Fee).MilliSatoshi,
                     BalanceOf(harness.Bob, ThreeNodeHarness.AliceBobChannelId));
        Assert.Equal(bobBc + (long)s_amount.MilliSatoshi, BalanceOf(harness.Bob, ThreeNodeHarness.BobCarolChannelId));
        var books = BooksSimulator.Of(await AccountingEventsAsync(harness.Bob));
        Assert.Equal((long)payment.Fee.MilliSatoshi, books[AccountRole.Rebalance]);
        Assert.Equal(0, books[AccountRole.Received]);
    }

    [Fact]
    public async Task Given_NoWayBack_When_BobPaysHisOwnInvoice_Then_ThePaymentFailsWithoutAnHtlc()
    {
        // Arrange: Bob holds no peer's channel_update, so no channel of his can take the payment back in
        await using var harness = await CreateAsync();
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "nowhere", null,
                                                                    TestContext.Current.CancellationToken);

        // Act
        var result = await PayAsync(harness, invoice.Bolt11!, new PayInvoiceOptions { Timeout = s_timeout });

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Contains("circular route", result.Payment.FailureReason);
        Assert.Equal(0, result.Attempts);
        var stored = await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(
                                                        invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open, stored!.Status);
    }

    [Fact]
    public async Task Given_ASettledInvoiceOfOurs_When_BobPaysIt_Then_Refused()
    {
        // Arrange: Alice pays Bob's invoice first
        await using var harness = await CreateAsync(alicePays: true);
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "paid", null, ct);
        var first = harness.Alice.Services.GetRequiredService<IPaymentService>()
                           .PayInvoiceAsync(invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = s_timeout }, ct);
        while (!first.IsCompleted)
            await harness.PumpAsync();
        Assert.Equal(PaymentStatus.Succeeded, (await first).Payment.Status);

        // Act / Assert
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.Bob.Services
                                                                             .GetRequiredService<IPaymentService>()
                                                                             .PayInvoiceAsync(invoice.Bolt11!, null,
                                                                                  new PayInvoiceOptions(), ct));
        Assert.Contains("Settled", refused.Message);
    }

    [Fact]
    public async Task Given_AnotherNodesInvoice_When_AnIncomingChannelIsPinned_Then_Refused()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "not ours", null, ct);

        // Act / Assert: --in only applies to a rebalance; the same channel both ways never does
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Bob.Services.GetRequiredService<IPaymentService>()
                                                                 .PayInvoiceAsync(invoice.Bolt11!, null,
                                                                      new PayInvoiceOptions
                                                                      {
                                                                          IncomingChannelId =
                                                                              ThreeNodeHarness.AliceBobChannelId
                                                                      }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Bob.Services.GetRequiredService<IPaymentService>()
                                                                 .PayInvoiceAsync(invoice.Bolt11!, null,
                                                                      new PayInvoiceOptions
                                                                      {
                                                                          OutgoingChannelId =
                                                                              ThreeNodeHarness.BobCarolChannelId,
                                                                          IncomingChannelId =
                                                                              ThreeNodeHarness.BobCarolChannelId
                                                                      }, ct));
    }

    /// <summary>The triangle, Bob with the payment service and the graph of the three channels (each side's policy is
    /// its node's routing options, as its channel_update says).</summary>
    private static Task<ThreeNodeHarness> CreateAsync(bool alicePays = false) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Bob.ConfigureServices = services =>
            {
                services.AddPaymentSendServices();
                var graphStore = new Mock<IGraphStore>();
                graphStore.Setup(s => s.GetSnapshot()).Returns(() => BuildGraph(h));
                services.AddSingleton(graphStore.Object);
            };
            if (alicePays)
                h.Alice.ConfigureServices = services => services.AddPaymentSendServices();
        }, carolAlice: true);

    private static Domain.Gossip.Graph.GraphSnapshot BuildGraph(ThreeNodeHarness harness)
    {
        SyntheticGraph.Policy Of(SwitchNode node)
        {
            var routing = node.Options.Routing;
            return new SyntheticGraph.Policy(routing.FeeBaseMsat, routing.FeeProportionalMillionths,
                                             routing.CltvExpiryDelta, 1_000_000_000, routing.HtlcMinimumMsat);
        }

        var capacity = ThreeNodeHarness.FundingSatoshis;
        return new SyntheticGraph { Timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
              .Channel(ThreeNodeHarness.AliceBobScid, harness.Alice.NodeId, harness.Bob.NodeId, capacity,
                       Of(harness.Alice), Of(harness.Bob))
              .Channel(ThreeNodeHarness.BobCarolScid, harness.Bob.NodeId, harness.Carol.NodeId, capacity,
                       Of(harness.Bob), Of(harness.Carol))
              .Channel(ThreeNodeHarness.CarolAliceScid, harness.Carol.NodeId, harness.Alice.NodeId, capacity,
                       Of(harness.Carol), Of(harness.Alice))
              .Build();
    }

    /// <summary>Bob receives <paramref name="peer"/>'s signed channel_update of their channel (the direct exchange the
    /// harness leaves out).</summary>
    private static void GiveBobThePeerUpdate(ThreeNodeHarness harness, SwitchNode peer, ChannelId channelId)
    {
        Assert.True(peer.Services.GetRequiredService<IChannelUpdateService>()
                        .TryGetLocalChannelUpdate(channelId, out var update));
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .HandleRemoteChannelUpdate(peer.NodeId, update!));
    }

    private static async Task<PayInvoiceResult> PayAsync(ThreeNodeHarness harness, string bolt11,
                                                         PayInvoiceOptions options)
    {
        var paying = harness.Bob.Services.GetRequiredService<IPaymentService>()
                            .PayInvoiceAsync(bolt11, null, options, TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow + s_timeout;
        while (!paying.IsCompleted && DateTime.UtcNow < deadline)
        {
            await harness.PumpAsync();
            await Task.WhenAny(paying, Task.Delay(10, TestContext.Current.CancellationToken));
        }

        await harness.PumpAsync();
        return await paying;
    }

    private static long BalanceOf(SwitchNode node, ChannelId channelId) =>
        (long)node.Channel(channelId).LocalBalance.MilliSatoshi;

    /// <summary>The accounting events <paramref name="node"/> saved (none is sealed in these tests).</summary>
    private static Task<IReadOnlyList<AccountingEventModel>> AccountingEventsAsync(SwitchNode node) =>
        node.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000));
}