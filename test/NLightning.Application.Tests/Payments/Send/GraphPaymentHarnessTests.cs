namespace NLightning.Application.Tests.Payments.Send;

using Bolt11.Models;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.ValueObjects;
using Harness;

/// <summary>
/// BOLT 7 plan G4-T3 proof, in process with real crypto, real onions and the production engine: Bob pays Erin's
/// invoice, which has no route hints, over a 3-channel route he finds in his gossip graph (Bob → Carol → David →
/// Erin), paying Carol's and David's fees exactly; and, with two Carol–David channels, retries around the one Carol
/// cannot forward over, learnt by mission control, without changing his graph.
/// </summary>
public class GraphPaymentHarnessTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private static PaymentHarness Harness(bool secondCarolDavid = false)
    {
        var harness = new PaymentHarness(new PaymentHarnessTopology(SecondCarolDavid: secondCarolDavid, Erin: true,
                                                                    BobUsesGraph: true));
        harness.Bob.GraphView = harness.BuildGraph((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return harness;
    }

    private static Task<PayInvoiceResult> PayAsync(PaymentHarness harness, string bolt11) =>
        harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(bolt11, null,
                                                                    new PayInvoiceOptions { Timeout = s_timeout },
                                                                    TestContext.Current.CancellationToken));

    /// <summary>What a hop keeps for forwarding <paramref name="amountMsat"/> under its harness policy (BOLT 7).</summary>
    private static ulong Fee(PaymentHarnessNode hop, ulong amountMsat) =>
        hop.Options.Routing.FeeBaseMsat + amountMsat * hop.Options.Routing.FeeProportionalMillionths / 1_000_000;

    [Fact]
    public async Task Given_ErinsInvoiceWithoutHints_When_BobPays_Then_TheGraphRouteDeliversItWithExactFees()
    {
        // Arrange
        using var harness = Harness();
        var erin = harness.Erin!;
        var ct = TestContext.Current.CancellationToken;
        var invoice = await erin.InvoiceService.CreateInvoiceAsync(s_amount, "graph", null, ct);
        var davidFee = Fee(harness.David, s_amount.MilliSatoshi);
        var carolFee = Fee(harness.Carol, s_amount.MilliSatoshi + davidFee);
        var bobBefore = harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;
        var erinBefore = erin.Channel(harness.DavidErin).LocalBalance.MilliSatoshi;

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert: Erin is not Bob's peer and hints nothing
        Assert.Empty(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints);
        var payment = result.Payment;
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(invoice.Preimage, payment.Preimage);
        Assert.Equal(1, result.Attempts);

        // Assert: the route and every hop's HTLC, BOLT 7 fees and CLTV deltas (Erin's c = 40, +3 safety blocks)
        Assert.Equal(1_050UL, davidFee);
        Assert.Equal(27_000UL, carolFee);
        Assert.Equal(davidFee + carolFee, payment.Fee.MilliSatoshi);
        var final = PaymentHarness.BlockHeight + 40 + 3;
        Assert.Equal(3, payment.Route.Count);
        Assert.Equal((harness.Carol.NodeId, PaymentHarness.ScidBobCarol, s_amount.MilliSatoshi + davidFee + carolFee,
                      final + 40 + 40),
                     (payment.Route[0].NodeId, payment.Route[0].ShortChannelId, payment.Route[0].Amount.MilliSatoshi,
                      payment.Route[0].CltvExpiry));
        Assert.Equal((harness.David.NodeId, PaymentHarness.ScidCarolDavid, s_amount.MilliSatoshi + davidFee,
                      final + 40),
                     (payment.Route[1].NodeId, payment.Route[1].ShortChannelId, payment.Route[1].Amount.MilliSatoshi,
                      payment.Route[1].CltvExpiry));
        Assert.Equal((erin.NodeId, PaymentHarness.ScidDavidErin, s_amount.MilliSatoshi, final),
                     (payment.Route[2].NodeId, payment.Route[2].ShortChannelId, payment.Route[2].Amount.MilliSatoshi,
                      payment.Route[2].CltvExpiry));

        // Assert: the money moved exactly
        Assert.Equal(bobBefore - s_amount.MilliSatoshi - davidFee - carolFee,
                     harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi);
        Assert.Equal(erinBefore + s_amount.MilliSatoshi, erin.Channel(harness.DavidErin).LocalBalance.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Accepted, (await erin.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);

        // Assert: mission control learnt that both channels after Bob's carried the payment
        Assert.True(harness.Bob.MissionControl.TryGetBounds(PaymentHarness.ScidDavidErin, harness.David.NodeId,
                                                            erin.NodeId, out var carried, out _));
        Assert.Equal(s_amount.MilliSatoshi, carried);
    }

    [Fact]
    public async Task Given_CarolCannotForwardOverOneChannel_When_BobPays_Then_TheRetryTakesTheOtherAndTheGraphIsUnchanged()
    {
        // Arrange: two Carol–David channels in Bob's graph; Carol fails every forward over the first one Bob tries
        using var harness = Harness(secondCarolDavid: true);
        var erin = harness.Erin!;
        var ct = TestContext.Current.CancellationToken;
        var graph = harness.Bob.GraphView;
        var policiesBefore = graph.Channels.Select(c => (c.ShortChannelId, c.Policy1, c.Policy2)).ToList();
        var invoice = await erin.InvoiceService.CreateInvoiceAsync(s_amount, "retry", null, ct);
        ShortChannelId? failing = null;
        harness.Carol.Switch.ForwardInterceptor = (_, forward) =>
        {
            failing ??= forward.OutgoingShortChannelId;
            return forward.OutgoingShortChannelId == failing ? FailureMessage.TemporaryChannelFailure() : null;
        };

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert: the second HTLC went over the other Carol–David channel
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(2, result.Attempts);
        var forwards = harness.Carol.Switch.Forwards.ToArray();
        Assert.Equal(2, forwards.Length);
        Assert.NotEqual(forwards[0].Forward.OutgoingShortChannelId, forwards[1].Forward.OutgoingShortChannelId);
        Assert.Equal(forwards[1].Forward.OutgoingShortChannelId, result.Payment.Route[1].ShortChannelId);

        // Assert: mission control bounds the failed channel below the HTLC Carol could not forward
        Assert.True(harness.Bob.MissionControl.TryGetBounds(failing!.Value, harness.Carol.NodeId,
                                                            harness.David.NodeId, out _, out var max));
        Assert.Equal(forwards[0].Forward.AmountToForward.MilliSatoshi, max);

        // Assert: every round read the same graph, and it still holds exactly the policies it started with
        Assert.True(harness.Bob.GraphReads.Count >= 2);
        Assert.All(harness.Bob.GraphReads, read => Assert.Same(graph, read));
        Assert.Equal(policiesBefore, graph.Channels.Select(c => (c.ShortChannelId, c.Policy1, c.Policy2)).ToList());
        Assert.True(graph.TryGetChannel(failing.Value, out var failed));
        Assert.False(failed.Policy1!.IsDisabled || failed.Policy2!.IsDisabled);
    }
}